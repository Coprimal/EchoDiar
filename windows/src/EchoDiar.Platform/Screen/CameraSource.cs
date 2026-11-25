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

    /// <summary>Stops the frames and releases the camera; an open in progress is dropped. Safe in any state.</summary>
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
    /// down by a whole step to at most <see cref="MaxWidth"/>; dropped when <paramref name="generation"/> is stale.
    /// </summary>
    private unsafe void Store(byte* source, int width, int height, int stride, int generation)
    {
        if (width <= 0 || height <= 0) return;
        var step = (width + MaxWidth - 1) / MaxWidth;
        var outWidth = width / step;
        var outHeight = height / step;
        if (outWidth == 0 || outHeight == 0) return;
        lock (_lock)
        {
            if (generation != _generation || _session is null) return;
            var length = outWidth * 4 * outHeight;
            if (_pixels.Length < length) _pixels = new byte[length];
            fixed (byte* destination = _pixels)
            {
                for (var y = 0; y < outHeight; y++)
                {
                    var from = (uint*)(source + (long)y * step * stride);
                    var to = (uint*)destination + y * outWidth;
                    for (var x = 0; x < outWidth; x++) to[x] = from[(outWidth - 1 - x) * step] | 0xFF000000;
                }
            }
            _width = outWidth;
            _height = outHeight;
            _number++;
        }
        try
        {
            FrameAvailable?.Invoke();
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
