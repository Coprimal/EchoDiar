using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace EchoDiar.Platform.Screen;

/// <summary>How <see cref="CameraSource.StartAsync"/> ended.</summary>
public enum CameraOpenStatus
{
    /// <summary>Frames are coming (or will in a moment).</summary>
    Started,

    /// <summary>No video capture device (a Release build; a DEBUG build uses the fake camera instead).</summary>
    NoCamera,

    /// <summary>Camera access is off for EchoDiar (Windows Settings › Privacy &amp; security › Camera).</summary>
    AccessDenied,

    /// <summary>The camera could not be opened or started; <see cref="CameraOpenResult.Message"/> says why.</summary>
    Failed,
}

/// <param name="Message">Why it failed (<see cref="CameraOpenStatus.Failed"/>), for the log; null otherwise.</param>
/// <param name="IsFake">The DEBUG fake camera delivers the frames. Always false in a Release build.</param>
public readonly record struct CameraOpenResult(CameraOpenStatus Status, string? Message = null, bool IsFake = false);

/// <summary>What <see cref="CameraSource.CopyLatest"/> copied.</summary>
/// <param name="Stride">Bytes between rows in the copy: always <c>Width * 4</c>.</param>
/// <param name="Number">The frame's sequence number: grows with every frame, across Start/Stop, never 0.</param>
public readonly record struct CameraFrameInfo(int Width, int Height, int Stride, long Number);

/// <summary>
/// The camera of the screen recording's camera bubble: the first color camera (front panel first), opened with
/// MediaCapture in <see cref="MediaCaptureSharingMode.SharedReadOnly"/> so an app that controls the camera (Teams,
/// Zoom) keeps it, read by a MediaFrameReader as BGRA8 at most <see cref="MaxWidth"/> pixels wide.
/// <para>Frames are stored mirrored horizontally (a mirror, as the macOS bubble's preview) and opaque (alpha 255, so
/// premultiplied too), top-down, <c>width * 4</c> bytes a row. Only the latest frame is kept; <see cref="FrameAvailable"/>
/// says a new one is there and <see cref="CopyLatest"/> copies it into the caller's buffer. Nothing is written to
/// disk.</para>
/// <para>"Blur background" (<see cref="SetBlur"/>, switchable while running): each frame is then delivered with
/// everything but the person (<see cref="PersonSegmenter"/>) blurred, at most <see cref="BlurMaxRate"/> a second. A
/// sharp frame is never delivered while it is on: the latest frame is dropped when it turns on, and when the segmenter
/// cannot be made or fails no frame comes at all and <see cref="BlurFailed"/> is raised (once per turn-on and open)
/// until blur is turned off.</para>
/// <para>DEBUG builds only: the fake camera (moving test frames, about 25 fps, same delivery path) is used when the
/// marker file <c>&lt;probes folder&gt;\fake-camera</c> exists when opening, or when there is no camera.</para>
/// <para>Thread-safe. Events are raised on background threads; handler exceptions are swallowed.</para>
/// </summary>
public sealed class CameraSource : IDisposable
{
    /// <summary>Frames wider than this are scaled down (enough for the bubble and its place in the video).</summary>
    public const int MaxWidth = 640;

#if DEBUG
    // The marker file, in the probes folder, that turns the fake camera on.
    private const string FakeCameraMarker = "fake-camera";

    private const int FakeWidth = 640;
    private const int FakeHeight = 480;
    private const int FakeIntervalMs = 40;

    private readonly string _probesFolder;
#endif

    private readonly Lock _lock = new();
    private Session? _session;
    private Task<CameraOpenResult>? _opening;
    private int _generation;
    private bool _disposed;

    // The latest frame, under _lock.
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private long _number;

    // Blur, under _lock: on, failed since the last turn-on or open, and bumped by both (a blurred frame of an older
    // epoch is dropped).
    private bool _blur;
    private bool _blurFailed;
    private int _blurEpoch;

    // The blur stage, under _blurGate (one frame at a time; a frame that comes while another is blurred is dropped).
    private readonly Lock _blurGate = new();
    private readonly BlurStage _stage = new();
    private PersonSegmenter? _segmenter;

    /// <param name="probesFolder">
    /// The App's probes folder (<c>AppPaths.Probes</c>), where a DEBUG build looks for the <c>fake-camera</c> marker.
    /// </param>
    public CameraSource(string probesFolder)
    {
        ArgumentNullException.ThrowIfNull(probesFolder);
#if DEBUG
        _probesFolder = probesFolder;
#endif
    }

    /// <summary>A new frame can be copied with <see cref="CopyLatest"/>. Raised on a background thread.</summary>
    public event Action? FrameAvailable;

    /// <summary>
    /// The running camera stopped by itself (unplugged, turned off, taken by another app): the source has already
    /// stopped. The argument is MediaCapture's message, for the log. Raised on a background thread.
    /// </summary>
    public event Action<string>? Failed;

    /// <summary>
    /// "Blur background" is on, but the person cannot be found (no model, ONNX Runtime missing, the model failed): no
    /// frame is delivered until <see cref="SetBlur"/> turns blur off. Raised once per turn-on and per open, on a
    /// background thread; the argument is why, for the log.
    /// </summary>
    public event Action<string>? BlurFailed;

    /// <summary>Blurred frames are at most this many a second (macOS BlurRenderer.maxRate).</summary>
    public const double BlurMaxRate = 15;

    /// <summary>"Blur background" is on (<see cref="SetBlur"/>).</summary>
    public bool Blur
    {
        get { lock (_lock) return _blur; }
    }

    /// <summary>
    /// Turns "Blur background" on or off, before <see cref="StartAsync"/> or while running. Turning it on drops the
    /// latest (sharp) frame at once, so <see cref="CopyLatest"/> gives nothing until the first blurred one; turning it
    /// off brings the sharp frames back from the next one. Any thread; never throws.
    /// </summary>
    public void SetBlur(bool on)
    {
        lock (_lock)
        {
            if (on == _blur) return;
            _blur = on;
            _blurEpoch++;
            if (!on) return;
            _blurFailed = false;
            _width = 0;
            _height = 0;
        }
    }

    /// <summary>Whether a camera (or the fake one) is open.</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _session is not null; }
    }

    /// <summary>
    /// Opens the camera and starts its frames. Started when already running; a call while opening shares that open.
    /// Never throws (but <see cref="ObjectDisposedException"/>): failures are in the result.
    /// </summary>
    public Task<CameraOpenResult> StartAsync()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) return Task.FromResult(new CameraOpenResult(CameraOpenStatus.Started, IsFake: _session.IsFake));
            if (_opening is not null) return _opening;
            var generation = ++_generation;
            // Off the caller's thread (MTA); the lock keeps OpenAsync from clearing _opening before it is set.
            return _opening = Task.Run(() => OpenAsync(generation));
        }
    }

    /// <summary>
    /// Stops the frames and releases the camera and the blur's model; an open in progress is dropped. Safe in any
    /// state; may wait for a frame being blurred.
    /// </summary>
    public void Stop()
    {
        Session? session;
        lock (_lock)
        {
            _generation++;
            _opening = null;
            session = _session;
            _session = null;
            _width = 0;
            _height = 0;
        }
        session?.Close(); // outside the lock: closing the reader may wait for a frame callback that takes it
        // The model is loaded again by the next open with blur on (a model gone meanwhile fails then). Taken after the
        // blur of a frame still under way; later callbacks are stale and leave the gate at once.
        lock (_blurGate)
        {
            _segmenter?.Dispose();
            _segmenter = null;
        }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        Stop();
    }

    /// <summary>
    /// Copies the latest frame into <paramref name="buffer"/> (replaced by a larger array when null or too small)
    /// when it is newer than <paramref name="newerThan"/> (a <see cref="CameraFrameInfo.Number"/>, 0 for any). Null
    /// when there is no such frame (none yet, or stopped).
    /// </summary>
    public CameraFrameInfo? CopyLatest(ref byte[]? buffer, long newerThan = 0)
    {
        lock (_lock)
        {
            if (_width == 0 || _number <= newerThan) return null;
            var length = _width * 4 * _height;
            if (buffer is null || buffer.Length < length) buffer = new byte[length];
            _pixels.AsSpan(0, length).CopyTo(buffer);
            return new CameraFrameInfo(_width, _height, _width * 4, _number);
        }
    }

    private async Task<CameraOpenResult> OpenAsync(int generation)
    {
        try
        {
#if DEBUG
            if (File.Exists(Path.Combine(_probesFolder, FakeCameraMarker))) return Commit(generation, Session.Fake(this, generation));
#endif
            var found = await FindCamera().ConfigureAwait(false);
            if (found is null)
            {
#if DEBUG
                return Commit(generation, Session.Fake(this, generation));
#else
                return new CameraOpenResult(CameraOpenStatus.NoCamera);
#endif
            }
            return await OpenCamera(generation, found.Value.Group, found.Value.Info).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new CameraOpenResult(CameraOpenStatus.Failed, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation) _opening = null;
            }
        }
    }

    /// <summary>The first color camera's source group and source, a front-panel one first; null when none.</summary>
    private static async Task<(MediaFrameSourceGroup Group, MediaFrameSourceInfo Info)?> FindCamera()
    {
        (MediaFrameSourceGroup, MediaFrameSourceInfo)? first = null;
        foreach (var group in await MediaFrameSourceGroup.FindAllAsync())
        {
            var info = group.SourceInfos.FirstOrDefault(IsColorVideo);
            if (info is null) continue;
            if (info.DeviceInformation?.EnclosureLocation?.Panel == Panel.Front) return (group, info);
            first ??= (group, info);
        }
        return first;
    }

    private static bool IsColorVideo(MediaFrameSourceInfo info) => info.SourceKind == MediaFrameSourceKind.Color
        && info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;

    private async Task<CameraOpenResult> OpenCamera(int generation, MediaFrameSourceGroup group, MediaFrameSourceInfo info)
    {
        var capture = new MediaCapture();
        MediaFrameReader? reader = null;
        try
        {
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                });
            }
            catch (UnauthorizedAccessException)
            {
                capture.Dispose();
                return new CameraOpenResult(CameraOpenStatus.AccessDenied);
            }

            if (!capture.FrameSources.TryGetValue(info.Id, out var source))
            {
                source = capture.FrameSources.Values.FirstOrDefault(s => IsColorVideo(s.Info));
            }
            if (source is null)
            {
                capture.Dispose();
                return new CameraOpenResult(CameraOpenStatus.Failed, "No color stream on the camera");
            }

            // SharedReadOnly cannot change the camera's format: the reader converts to BGRA8 and scales instead. A
            // reader that refuses the size is asked for BGRA8 alone (Deliver scales such frames down).
            var size = OutputSize(source.CurrentFormat?.VideoFormat);
            try
            {
                reader = size is { } s
                    ? await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, s)
                    : await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            }
            catch (Exception) when (size is not null)
            {
                reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            }
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += (sender, _) => OnFrameArrived(sender, generation);
            capture.Failed += (_, e) => OnCaptureFailed(generation, e.Message);

            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.Dispose();
                capture.Dispose();
                return new CameraOpenResult(CameraOpenStatus.Failed, $"MediaFrameReader.StartAsync: {status}");
            }
            return Commit(generation, Session.Camera(capture, reader));
        }
        catch (UnauthorizedAccessException)
        {
            reader?.Dispose();
            capture.Dispose();
            return new CameraOpenResult(CameraOpenStatus.AccessDenied);
        }
        catch
        {
            reader?.Dispose();
            capture.Dispose();
            throw;
        }
    }

    /// <summary>The camera's size scaled to <see cref="MaxWidth"/> (even sides); null when it is not wider.</summary>
    private static BitmapSize? OutputSize(VideoMediaFrameFormat? format)
    {
        if (format is null || format.Width <= MaxWidth || format.Height == 0) return null;
        var height = (uint)Math.Max(2, Math.Round(MaxWidth * (double)format.Height / format.Width / 2) * 2);
        return new BitmapSize { Width = MaxWidth, Height = height };
    }

    /// <summary>Keeps <paramref name="session"/> as the running one, or closes it when a Stop came meanwhile.</summary>
    private CameraOpenResult Commit(int generation, Session session)
    {
        lock (_lock)
        {
            if (generation == _generation && !_disposed)
            {
                _session = session;
                // Every open tries the blur again (and says once more when it fails).
                _blurFailed = false;
                _blurEpoch++;
                session.Begin();
                return new CameraOpenResult(CameraOpenStatus.Started, IsFake: session.IsFake);
            }
        }
        session.Close();
        return new CameraOpenResult(CameraOpenStatus.Failed, "Stopped while opening");
    }

    private void OnFrameArrived(MediaFrameReader reader, int generation)
    {
        try
        {
            using var frame = reader.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            if (bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8)
            {
                Deliver(bitmap, generation);
            }
            else
            {
                using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                Deliver(converted, generation);
            }
        }
        catch (Exception)
        {
            // A frame that cannot be read is dropped; the next one may work. Never thrown into MediaFrameReader.
        }
    }

    private unsafe void Deliver(SoftwareBitmap bitmap, int generation)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var unknown = MarshalInterface<IMemoryBufferReference>.FromManaged(reference);
        try
        {
            var access = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(unknown);
            try
            {
                access.GetBuffer(out var data, out var capacity);
                // Only what Store reads: the last row need not be padded to the stride.
                if (plane.Width <= 0 || plane.Height <= 0 || plane.Stride < plane.Width * 4
                    || (long)plane.StartIndex + (long)plane.Stride * (plane.Height - 1) + plane.Width * 4L > capacity) return;
                Store(data + plane.StartIndex, plane.Width, plane.Height, plane.Stride, generation);
            }
            finally
            {
                Marshal.ReleaseComObject(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Keeps a BGRA frame (rows <paramref name="stride"/> bytes apart) as the latest one, mirrored, opaque and scaled
    /// down by a whole step to at most <see cref="MaxWidth"/>, its background blurred while <see cref="Blur"/> is on;
    /// dropped when <paramref name="generation"/> is stale.
    /// </summary>
    private unsafe void Store(byte* source, int width, int height, int stride, int generation)
    {
        if (width <= 0 || height <= 0) return;
        var step = (width + MaxWidth - 1) / MaxWidth;
        var outWidth = width / step;
        var outHeight = height / step;
        if (outWidth == 0 || outHeight == 0) return;
        var length = outWidth * 4 * outHeight;
        bool blur;
        lock (_lock)
        {
            if (generation != _generation || _session is null) return;
            blur = _blur;
            if (!blur)
            {
                if (_pixels.Length < length) _pixels = new byte[length];
                fixed (byte* destination = _pixels) Mirror(source, stride, step, outWidth, outHeight, destination);
                _width = outWidth;
                _height = outHeight;
                _number++;
            }
            else if (_blurFailed)
            {
                return;
            }
        }
        // Blur on (checked again before keeping the result): outside _lock, which CopyLatest waits for.
        if (blur && !StoreBlurred(source, stride, step, outWidth, outHeight, generation)) return;
        try
        {
            FrameAvailable?.Invoke();
        }
        catch (Exception)
        {
            // A consumer's failure never reaches the camera's thread.
        }
    }

    /// <summary>The frame mirrored, opaque and scaled down by <paramref name="step"/> into <paramref name="destination"/>.</summary>
    private static unsafe void Mirror(byte* source, int stride, int step, int outWidth, int outHeight, byte* destination)
    {
        for (var y = 0; y < outHeight; y++)
        {
            var from = (uint*)(source + (long)y * step * stride);
            var to = (uint*)destination + y * outWidth;
            for (var x = 0; x < outWidth; x++) to[x] = from[(outWidth - 1 - x) * step] | 0xFF000000;
        }
    }

    /// <summary>
    /// The blur stage (camera thread): the mirrored frame with its background blurred becomes the latest one, unless
    /// blur was turned off, the open changed or the source stopped meanwhile. A frame is skipped while another is
    /// blurred or within the <see cref="BlurMaxRate"/> interval. A segmenter that cannot be made or fails turns the
    /// frames off (<see cref="Fail"/>). True when a frame was kept.
    /// </summary>
    private unsafe bool StoreBlurred(byte* source, int stride, int step, int outWidth, int outHeight, int generation)
    {
        int epoch;
        lock (_lock)
        {
            if (generation != _generation || _session is null || !_blur || _blurFailed) return false;
            epoch = _blurEpoch;
        }
        if (!_blurGate.TryEnter()) return false;
        try
        {
            // Checked again inside the gate: a Stop (which bumps the generation, then releases the segmenter under
            // this gate) since the check above must not see a segmenter made again.
            lock (_lock)
            {
                if (generation != _generation || _session is null || _disposed) return false;
            }
            if (!_stage.Due(epoch)) return false;
            var raw = _stage.Frame(outWidth, outHeight);
            fixed (byte* destination = raw) Mirror(source, stride, step, outWidth, outHeight, destination);
            if (_segmenter is null)
            {
                if (!PersonSegmenter.TryCreate(out var made, out var error))
                {
                    Fail(epoch, $"the person segmenter could not be made: {error}");
                    return false;
                }
                _segmenter = made;
            }
            try
            {
                _stage.Process(_segmenter, epoch);
            }
            catch (Exception e)
            {
                // Made again at the next try (turn-on or open).
                _segmenter.Dispose();
                _segmenter = null;
                Fail(epoch, $"the person segmenter failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
            lock (_lock)
            {
                if (generation != _generation || _session is null || !_blur || epoch != _blurEpoch) return false;
                var length = outWidth * 4 * outHeight;
                if (_pixels.Length < length) _pixels = new byte[length];
                _stage.Output.AsSpan(0, length).CopyTo(_pixels);
                _width = outWidth;
                _height = outHeight;
                _number++;
            }
            return true;
        }
        catch (Exception)
        {
            return false; // never thrown into the camera's callback
        }
        finally
        {
            _blurGate.Exit();
        }
    }

    /// <summary>Blur of <paramref name="epoch"/> cannot work: no frame from now on, and <see cref="BlurFailed"/> once.</summary>
    private void Fail(int epoch, string message)
    {
        lock (_lock)
        {
            if (epoch != _blurEpoch || !_blur || _blurFailed) return;
            _blurFailed = true;
            _width = 0;
            _height = 0;
        }
        try
        {
            BlurFailed?.Invoke(message);
        }
        catch (Exception)
        {
            // A consumer's failure never reaches the camera's thread.
        }
    }

    private void OnCaptureFailed(int generation, string message)
    {
        // Released off MediaCapture's callback, which closing the capture could wait for.
        _ = Task.Run(() =>
        {
            Session? session;
            lock (_lock)
            {
                if (generation != _generation || _session is null) return;
                _generation++;
                session = _session;
                _session = null;
                _width = 0;
                _height = 0;
            }
            session.Close();
            try
            {
                Failed?.Invoke(message);
            }
            catch (Exception)
            {
                // A consumer's failure never reaches the camera's thread.
            }
        });
    }

#if DEBUG
    /// <summary>
    /// One fake frame (DEBUG): a gradient whose red follows the time, a white disc that bounces across it and, along
    /// the bottom, the frame number as 16 bits (white squares are ones). It goes through <see cref="Store"/>, so it
    /// is mirrored as a camera frame.
    /// </summary>
    private unsafe void FakeTick(Session session, int generation)
    {
        if (Interlocked.Exchange(ref session.FakeBusy, 1) == 1) return; // the previous tick still runs
        try
        {
            var tick = ++session.FakeTicks;
            var pixels = session.FakePixels ??= new uint[FakeWidth * FakeHeight];
            var red = (uint)(tick * 4 % 256);
            var t = tick * FakeIntervalMs / 1000.0;
            var cx = FakeWidth / 2 + (FakeWidth / 2 - 80) * Math.Sin(t * 1.3);
            var cy = FakeHeight / 2 + (FakeHeight / 2 - 100) * Math.Cos(t * 0.9);
            const int radius = 60;
            const int bit = 24;
            for (var y = 0; y < FakeHeight; y++)
            {
                var green = (uint)(y * 255 / FakeHeight);
                for (var x = 0; x < FakeWidth; x++)
                {
                    var blue = (uint)(x * 255 / FakeWidth);
                    double dx = x - cx, dy = y - cy;
                    var color = dx * dx + dy * dy <= radius * radius
                        ? 0xFFFFFFFFu
                        : 0xFF000000u | red << 16 | green << 8 | blue;
                    if (y >= FakeHeight - bit - 8 && y < FakeHeight - 8 && x >= 8 && x < 8 + 16 * bit)
                    {
                        var index = (x - 8) / bit;
                        color = ((tick >> (15 - index)) & 1) == 1 ? 0xFFFFFFFFu : 0xFF000000u;
                    }
                    pixels[y * FakeWidth + x] = color;
                }
            }
            fixed (uint* data = pixels) Store((byte*)data, FakeWidth, FakeHeight, FakeWidth * 4, generation);
        }
        catch (Exception)
        {
            // Never thrown into the timer.
        }
        finally
        {
            Volatile.Write(ref session.FakeBusy, 0);
        }
    }
#endif

    /// <summary>
    /// The blur stage's buffers and steps, used under <c>_blurGate</c> only: the mirrored frame (<see cref="Frame"/>),
    /// the person mask (made every <see cref="SegmentEvery"/> frames, the last one reused between), the background
    /// (a quarter-size copy box-blurred three times each way, about macOS's Gaussian sigma of 20 px at 640 wide) and
    /// the result (<see cref="Output"/>): the sharp frame where the mask is high over the background scaled back up.
    /// Buffers grow with the frame size and are kept; nothing is allocated per frame.
    /// </summary>
    private sealed class BlurStage
    {
        /// <summary>The person is found again every this many blurred frames (mask lag against CPU).</summary>
        private const int SegmentEvery = 2;

        /// <summary>The box radius on the small copy; three passes give a sigma of about 5.5 small pixels.</summary>
        private const int Radius = 5;

        private byte[] _raw = [];
        private byte[] _mask = [];
        private byte[] _small = [];
        private byte[] _scratch = [];
        private int[] _x0 = [];
        private int[] _x1 = [];
        private float[] _fx = [];
        private int _width;
        private int _height;
        private int _maskWidth;
        private int _maskHeight;
        private int _maskEpoch = -1;
        private int _frames;
        private int _epoch = -1;
        private long _last;

        /// <summary>The last blurred frame, BGRA, <c>width * 4</c> bytes a row.</summary>
        public byte[] Output { get; private set; } = [];

        /// <summary>
        /// A frame of <paramref name="epoch"/> may be blurred now: the first of an epoch always, else once the
        /// <see cref="BlurMaxRate"/> interval (less 10 %, so a 30 fps camera keeps one in two) has passed.
        /// </summary>
        public bool Due(int epoch)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (epoch == _epoch && now - _last < System.Diagnostics.Stopwatch.Frequency * 0.9 / BlurMaxRate) return false;
            _epoch = epoch;
            _last = now;
            return true;
        }

        /// <summary>The buffer for the next mirrored frame, at least <paramref name="width"/> x <paramref name="height"/>.</summary>
        public byte[] Frame(int width, int height)
        {
            var length = width * 4 * height;
            if (_raw.Length < length) _raw = new byte[length];
            if (Output.Length < length) Output = new byte[length];
            _width = width;
            _height = height;
            return _raw;
        }

        /// <summary>Blurs the frame given by <see cref="Frame"/> into <see cref="Output"/>; throws when segmenting fails.</summary>
        public void Process(PersonSegmenter segmenter, int epoch)
        {
            int width = _width, height = _height;
            if (_mask.Length < width * height) _mask = new byte[width * height];
            // A new size or epoch (turn-on, open) never reuses an older mask.
            if (_maskEpoch != epoch || _maskWidth != width || _maskHeight != height || _frames % SegmentEvery == 0)
            {
                _maskEpoch = -1;
                segmenter.Segment(_raw.AsSpan(0, width * 4 * height), width, height, width * 4, _mask);
                _maskEpoch = epoch;
                _maskWidth = width;
                _maskHeight = height;
                _frames = 0;
            }
            _frames++;
            var factor = Math.Max(1, (width + 80) / 160);
            int smallWidth = Math.Max(1, width / factor), smallHeight = Math.Max(1, height / factor);
            Shrink(width, height, factor, smallWidth, smallHeight);
            for (var pass = 0; pass < 3; pass++)
            {
                Box(_small, _scratch, smallWidth, smallHeight, 4, smallWidth * 4); // rows
                Box(_scratch, _small, smallHeight, smallWidth, smallWidth * 4, 4); // columns
            }
            Compose(width, height, factor, smallWidth, smallHeight);
        }

        /// <summary>The frame averaged over <paramref name="factor"/>-pixel blocks into <c>_small</c> (BGR; A unused).</summary>
        private void Shrink(int width, int height, int factor, int smallWidth, int smallHeight)
        {
            var length = smallWidth * 4 * smallHeight;
            if (_small.Length < length) _small = new byte[length];
            if (_scratch.Length < length) _scratch = new byte[length];
            var count = factor * factor;
            for (var sy = 0; sy < smallHeight; sy++)
            {
                for (var sx = 0; sx < smallWidth; sx++)
                {
                    int b = 0, g = 0, r = 0;
                    for (var y = sy * factor; y < (sy + 1) * factor; y++)
                    {
                        // A frame lower than the factor repeats its last row.
                        var i = (Math.Min(y, height - 1) * width + sx * factor) * 4;
                        for (var x = 0; x < factor; x++, i += 4)
                        {
                            b += _raw[i];
                            g += _raw[i + 1];
                            r += _raw[i + 2];
                        }
                    }
                    var o = (sy * smallWidth + sx) * 4;
                    _small[o] = (byte)(b / count);
                    _small[o + 1] = (byte)(g / count);
                    _small[o + 2] = (byte)(r / count);
                }
            }
        }

        /// <summary>
        /// One box pass of radius <see cref="Radius"/> over B, G and R along <paramref name="lines"/> lines of
        /// <paramref name="length"/> pixels (<paramref name="step"/> bytes apart; lines start <paramref name="lineStep"/>
        /// bytes apart), the edge pixels repeated.
        /// </summary>
        private static void Box(byte[] from, byte[] to, int length, int lines, int step, int lineStep)
        {
            const int window = 2 * Radius + 1;
            for (var line = 0; line < lines; line++)
            {
                var start = line * lineStep;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var k = -Radius; k <= Radius; k++) sum += from[start + Math.Clamp(k, 0, length - 1) * step + c];
                    for (var i = 0; i < length; i++)
                    {
                        to[start + i * step + c] = (byte)((sum + window / 2) / window);
                        sum += from[start + Math.Min(i + Radius + 1, length - 1) * step + c]
                            - from[start + Math.Max(i - Radius, 0) * step + c];
                    }
                }
            }
        }

        /// <summary>The sharp frame over the background scaled back up (bilinear), by the mask, into <see cref="Output"/>.</summary>
        private void Compose(int width, int height, int factor, int smallWidth, int smallHeight)
        {
            if (_x0.Length < width)
            {
                _x0 = new int[width];
                _x1 = new int[width];
                _fx = new float[width];
            }
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5f) / factor - 0.5f, 0, smallWidth - 1);
                _x0[x] = (int)sx;
                _x1[x] = Math.Min(_x0[x] + 1, smallWidth - 1);
                _fx[x] = sx - _x0[x];
            }
            var output = Output;
            for (var y = 0; y < height; y++)
            {
                var sy = Math.Clamp((y + 0.5f) / factor - 0.5f, 0, smallHeight - 1);
                var y0 = (int)sy;
                var y1 = Math.Min(y0 + 1, smallHeight - 1);
                var fy = sy - y0;
                int row0 = y0 * smallWidth * 4, row1 = y1 * smallWidth * 4;
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var m = _mask[y * width + x] / 255f;
                    int a = _x0[x] * 4, b = _x1[x] * 4;
                    var fx = _fx[x];
                    for (var c = 0; c < 3; c++)
                    {
                        var top = _small[row0 + a + c] + (_small[row0 + b + c] - _small[row0 + a + c]) * fx;
                        var bottom = _small[row1 + a + c] + (_small[row1 + b + c] - _small[row1 + a + c]) * fx;
                        var background = top + (bottom - top) * fy;
                        output[i + c] = (byte)(background + (_raw[i + c] - background) * m + 0.5f);
                    }
                    output[i + 3] = 0xFF;
                }
            }
        }
    }

    /// <summary>What one open holds: the MediaCapture and its reader, or (DEBUG) the fake camera's timer.</summary>
    private sealed class Session
    {
        private readonly MediaCapture? _capture;
        private readonly MediaFrameReader? _reader;
#if DEBUG
        private readonly Timer? _timer;
        public int FakeBusy;
        public long FakeTicks;
        public uint[]? FakePixels;
#endif

        private Session(MediaCapture? capture, MediaFrameReader? reader, bool isFake)
        {
            _capture = capture;
            _reader = reader;
            IsFake = isFake;
        }

#if DEBUG
        private Session(CameraSource owner, int generation) : this(null, null, true)
        {
            // Created stopped; Begin starts it once the session is kept.
            _timer = new Timer(_ => owner.FakeTick(this, generation), null, Timeout.Infinite, Timeout.Infinite);
        }

        public static Session Fake(CameraSource owner, int generation) => new(owner, generation);
#endif

        public bool IsFake { get; }

        public static Session Camera(MediaCapture capture, MediaFrameReader reader) => new(capture, reader, false);

        /// <summary>Starts the fake camera's timer (the camera's reader already runs).</summary>
        public void Begin()
        {
#if DEBUG
            _timer?.Change(0, FakeIntervalMs);
#endif
        }

        /// <summary>Releases everything; late callbacks are dropped by the generation check.</summary>
        public void Close()
        {
#if DEBUG
            _timer?.Dispose();
#endif
            try
            {
                _reader?.Dispose();
            }
            catch (Exception)
            {
                // Released anyway with the capture below.
            }
            try
            {
                _capture?.Dispose();
            }
            catch (Exception)
            {
                // Nothing more to release.
            }
        }
    }

    /// <summary>Byte access to a locked <see cref="SoftwareBitmap"/> buffer (IMemoryBufferByteAccess).</summary>
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
