using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace EchoDiar.Platform.Screen;

public enum ScreenSourceKind
{
    Display,
    Window,
    Area,
}

/// <summary>
/// What a screen video captures (macOS <c>ScreenSource</c>): a monitor (HMONITOR), a top-level window (HWND), or an
/// area of a monitor (HMONITOR plus <paramref name="Region"/> in that monitor's pixels, top-left origin; the monitor is
/// captured and cropped to it).
/// </summary>
public readonly record struct ScreenSource(ScreenSourceKind Kind, nint Handle, ScreenGeometry.Rect Region)
{
    public static ScreenSource Display(nint monitor) => new(ScreenSourceKind.Display, monitor, default);

    public static ScreenSource Window(nint window) => new(ScreenSourceKind.Window, window, default);

    public static ScreenSource Area(nint monitor, ScreenGeometry.Rect area) => new(ScreenSourceKind.Area, monitor, area);
}

/// <summary>Why the video ended before <see cref="ScreenRecorder.StopAsync"/> (macOS <c>ScreenRecorder.EndEvent</c>).</summary>
public enum ScreenRecorderEnd
{
    /// <summary>The captured window closed or the monitor went away (<c>GraphicsCaptureItem.Closed</c>).</summary>
    StreamStopped,

    /// <summary>Capturing, composing or encoding failed (<see cref="ScreenRecorderStats.LastError"/> says why).</summary>
    WriteFailed,

    /// <summary>
    /// The video could not go on after a pause. Not raised by this recorder: capture keeps running while paused, so
    /// resuming cannot fail; kept for the controller's notices (macOS <c>resume()</c> restarts its stream).
    /// </summary>
    ResumeFailed,

    /// <summary>Free space of the video folder fell under the minimum.</summary>
    LowDisk,
}

/// <summary>The free space of the video folder is under the minimum (macOS <c>ScreenRecorderError.lowDisk</c>).</summary>
public sealed class ScreenLowDiskException(long availableBytes)
    : IOException($"Only {availableBytes} bytes free for the screen video")
{
    public long AvailableBytes { get; } = availableBytes;
}

/// <summary>Arrival latency of capture frames (now minus <c>SystemRelativeTime</c>), in milliseconds.</summary>
public readonly record struct ScreenLatency(double Median, double Max, double Min);

/// <summary>
/// Sparse statistics of a raw captured frame (every 16th pixel of every 16th row): mean red, green and blue (0..255),
/// the smallest and largest alpha, the number of pixels read, and whether the frame is black: at most 1 % of them have
/// any red, green or blue (the captured pointer may cover a few).
/// </summary>
public readonly record struct ScreenFrameSample(
    double MeanR, double MeanG, double MeanB, int MinAlpha, int MaxAlpha, int Samples, bool Black);

/// <summary>
/// One capture session (a Windows.Graphics.Capture session of one source), for the probe: when it started (ms since
/// the process started), the frames that arrived, the first one's arrival (ms after the start) and raw statistics
/// (<paramref name="SampleError"/> when they could not be read). <paramref name="Restarted"/>: its first frame was
/// black, so it was not written and the session was replaced once by a new one, the next entry.
/// </summary>
public sealed record ScreenCaptureSession(
    ScreenSourceKind Kind, double StartedMs, int Arrivals, double? FirstArrivalMs, ScreenFrameSample? FirstFrame,
    string? SampleError, bool Restarted);

/// <summary>
/// What the recorder did so far, for the probe. <paramref name="FramesWritten"/> and
/// <paramref name="PlaceholdersWritten"/> count samples given to the encoder (the stop pad included);
/// <paramref name="LatencyMs"/> is null before the first frame; <paramref name="Sessions"/> in start order.
/// </summary>
public sealed record ScreenRecorderStats(
    VideoCodec? Codec, int Width, int Height, CaptureDeviceKind? DeviceKind, string? QualityError,
    int FramesWritten, int PlaceholdersWritten, int LatencySamples, ScreenLatency? LatencyMs, string? LastError,
    IReadOnlyList<ScreenCaptureSession> Sessions);

/// <summary>
/// Records screen sources into one video-only mp4 on the audio file's time base (macOS <c>ScreenRecorder.swift</c>):
/// Windows.Graphics.Capture frames, composed by <see cref="FrameCompositor"/> into the output size fixed by the first
/// source, timed by <see cref="VideoTimeline"/> at the engine's file times and written by <see cref="VideoEncoder"/>.
/// It never touches the audio: a video that ends never stops the recording.
/// </summary>
/// <remarks>
/// <para>One file per recording. <see cref="ScreenOffAsync"/> closes the capture and holds the placeholder until
/// <see cref="ScreenOnAsync"/> starts a capture of any source into the same file and size (aspect-fitted, as are
/// window resizes). <see cref="Pause"/> and <see cref="Resume"/> leave the capture running: frames have no file time
/// while the engine is paused and are not written.</para>
/// <para>Times: the file starts with the placeholder at 0. The first frame of each capture is written at the file time
/// of its request (the moment <see cref="StartAsync"/> or <see cref="ScreenOnAsync"/> was called), not at its later
/// arrival, so the picture starts when the screen was turned on; a start within one frame interval of 0 is written at
/// 0, so a video that starts with the recording shows no placeholder at all. Windows.Graphics.Capture only sends a
/// frame when the screen changes, so a frame dropped by <see cref="VideoTimeline"/> (too close to the last one) stays
/// pending, and a 1/fps timer feeds it at the current file time: the newest picture is always written.</para>
/// <para>Threading: the device, compositor and encoder are bound to the MTA, so every public method that touches them
/// runs on the thread pool and returns a task; those calls run one at a time, in call order. Capture callbacks and
/// timers share one lock with them. <see cref="Ended"/> is raised once, on a background thread.</para>
/// </remarks>
public sealed class ScreenRecorder
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int LatencyCapacity = 100_000; // median over the first ~1.8 h at 15 fps; min and max over all
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(10); // macOS diskCheckInterval
    private static readonly long ProcessStart = ProcessStartTicks(); // QPC, for ScreenCaptureSession.StartedMs

    private enum State { Idle, Running, Stopped }

    private readonly object _gate = new();
    private readonly object _statsGate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);

    // Under _gate
    private State _state;
    private bool _ended;
    private CaptureDevice? _device;
    private FrameCompositor? _compositor;
    private VideoEncoder? _encoder;
    private VideoTimeline? _timeline;
    private Func<long, double?> _fileTime = static _ => null;
    private byte[] _placeholder = [];
    private byte[] _picture = [];
    private bool _heldIsPlaceholder;
    private bool _pending; // the compositor holds a newer frame than the held picture, not fed yet
    private bool _awaitingFirst; // the capture has not fed a frame yet
    private double? _anchor; // file time of the capture's request, for its first frame
    private double? _lastFileTime; // the last file time seen, for a screen-off or end while paused
    private Capture? _capture;
    private int _generation;
    private bool _showsCursor;
    private string _folder = "";
    private long _minimumFreeBytes;
    private Timer? _frameTimer;
    private Timer? _diskTimer;
    private volatile bool _paused;

    /// <summary>Completed once <see cref="End"/> has finished the file, before <see cref="Ended"/> is raised.</summary>
    private readonly TaskCompletionSource _endFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Under _statsGate
    private VideoCodec? _codec;
    private ScreenGeometry.Size _size;
    private CaptureDeviceKind? _deviceKind;
    private string? _qualityError;
    private int _framesWritten;
    private int _placeholdersWritten;
    private readonly List<double> _latencies = [];
    private int _latencySamples;
    private double _latencyMax = double.MinValue;
    private double _latencyMin = double.MaxValue;
    private string? _lastError;
    private readonly List<SessionLog> _sessions = [];

    /// <summary>
    /// Raised once when the video ends before <see cref="StopAsync"/>; the file is already finished (padded to the
    /// last file time and finalized, so it plays). Raised on a background thread; handlers must not throw.
    /// </summary>
    public event Action<ScreenRecorderEnd>? Ended;

    public ScreenRecorderStats Stats
    {
        get
        {
            lock (_statsGate)
            {
                ScreenLatency? latency = null;
                if (_latencies.Count > 0)
                {
                    var sorted = _latencies.ToArray();
                    Array.Sort(sorted);
                    var mid = sorted.Length / 2;
                    var median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
                    latency = new ScreenLatency(median, _latencyMax, _latencyMin);
                }
                return new ScreenRecorderStats(_codec, _size.Width, _size.Height, _deviceKind, _qualityError,
                    _framesWritten, _placeholdersWritten, _latencySamples, latency, _lastError,
                    _sessions.Select(session => session.ToRecord()).ToArray());
            }
        }
    }

    /// <summary>
    /// Creates the mp4 at <paramref name="path"/> (replaced) and starts capturing <paramref name="source"/> (macOS
    /// <c>start(source:fps:quality:showsCursor:showsClicks:url:...)</c>). The output size is the source's, scaled
    /// down under the H.264 limit when the codec is H.264 (HEVC when an encoder and a decoder exist), even.
    /// </summary>
    /// <param name="fps">The frame rate: the encoder's, and the minimum time between written frames.</param>
    /// <param name="quality">0..1, the encoder's constant quality.</param>
    /// <param name="showsCursor"><c>GraphicsCaptureSession.IsCursorCaptureEnabled</c>.</param>
    /// <param name="showsClicks">Accepted; click rings are not drawn yet.</param>
    /// <param name="minimumFreeBytes">Below this free space of the video folder the start fails with
    /// <see cref="ScreenLowDiskException"/>, and a running video ends with <see cref="ScreenRecorderEnd.LowDisk"/>.</param>
    /// <param name="fileTime">The engine's file time of a QPC time in 100 ns units, null while paused or idle
    /// (<c>RecordingEngine.FileTime</c>); called off the UI thread.</param>
    /// <param name="placeholderText">Already localized ("Screen recording off").</param>
    /// <exception cref="InvalidOperationException">Started before.</exception>
    /// <exception cref="ScreenLowDiskException">Not enough free space; nothing was created.</exception>
    /// <remarks>On any failure the file is removed and the recorder can be started again.</remarks>
    public Task StartAsync(ScreenSource source, int fps, double quality, bool showsCursor, bool showsClicks, string path,
        long minimumFreeBytes, Func<long, double?> fileTime, string placeholderText)
    {
        _ = showsClicks; // T5.3
        var requested = Now();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentNullException.ThrowIfNull(fileTime);
        return Serialized(() => Start(source, fps, quality, showsCursor, path, minimumFreeBytes, fileTime,
            placeholderText, requested));
    }

    /// <summary>
    /// Turns the screen off: closes the capture first, then holds the placeholder from <paramref name="fileTime"/>
    /// (null: the engine's file time once the capture is closed; while paused, the last one seen) until
    /// <see cref="ScreenOnAsync"/> or the end. No-op unless started with the screen on, or once ended.
    /// </summary>
    public Task ScreenOffAsync(double? fileTime = null) => Serialized(() => ScreenOff(fileTime));

    /// <summary>
    /// Turns the screen on again with <paramref name="source"/>: a new capture into the same file and output size
    /// (aspect-fitted). Throws <see cref="InvalidOperationException"/> unless started, the screen off and not ended;
    /// on a failure to capture the source the screen stays off.
    /// </summary>
    public Task ScreenOnAsync(ScreenSource source)
    {
        var requested = Now();
        return Serialized(() => ScreenOn(source, requested));
    }

    /// <summary>Frames are not written until <see cref="Resume"/>; the capture keeps running.</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    /// <summary>
    /// Closes the capture, pads the last picture to <paramref name="fileTime"/> and finalizes the file (macOS
    /// <c>stop(at:)</c>). Pass the engine's file time read just before the audio stops, so the video ends with the
    /// audio; null reads it here (once the engine has stopped or while paused: the last file time seen). No-op when
    /// not started or after <see cref="Ended"/>; when the video is ending on its own meanwhile, completes once that end
    /// has finished the file. Throws when the last write or the finalize failed (the error is also in
    /// <see cref="ScreenRecorderStats.LastError"/>).
    /// </summary>
    public Task StopAsync(double? fileTime = null) => Serialized(() => Stop(fileTime));

    // Operations

    private void Start(ScreenSource source, int fps, double quality, bool showsCursor, string path,
        long minimumFreeBytes, Func<long, double?> fileTime, string placeholderText, long requested)
    {
        lock (_gate)
        {
            if (_state != State.Idle) throw new InvalidOperationException("The screen recorder was started before");
        }

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full) ?? full;
        var available = FreeSpace(folder);
        if (ScreenGeometry.IsLowDisk(available, minimumFreeBytes)) throw new ScreenLowDiskException(available!.Value);

        CaptureDevice? device = null;
        VideoEncoder? encoder = null;
        FrameCompositor? compositor = null;
        try
        {
            var codec = VideoEncoder.Detect().Codec;
            device = CaptureDevice.Create();
            var (item, sourceSize) = Item(source);
            var size = ScreenGeometry.OutputSize(sourceSize, codec == VideoCodec.H264 ? ScreenGeometry.H264Limit : sourceSize);
            encoder = new VideoEncoder(full, size.Width, size.Height, fps, quality, codec);
            compositor = new FrameCompositor(device, size, placeholderText);
            var placeholder = new byte[size.Width * 4 * size.Height];
            compositor.Compose(null, default);
            compositor.ReadPixels(placeholder);

            lock (_statsGate)
            {
                _codec = codec;
                _size = size;
                _deviceKind = device.Kind;
                _qualityError = encoder.QualityError;
            }
            lock (_gate)
            {
                _device = device;
                _encoder = encoder;
                _compositor = compositor;
                _timeline = new VideoTimeline(fps);
                _fileTime = fileTime;
                _placeholder = placeholder;
                _picture = new byte[placeholder.Length];
                _heldIsPlaceholder = true;
                _showsCursor = showsCursor;
                _folder = folder;
                _minimumFreeBytes = minimumFreeBytes;
                _lastFileTime = fileTime(requested);
                // A start within one frame interval of 0 starts the picture at 0: no placeholder flash.
                _anchor = _lastFileTime < _timeline.FrameInterval ? 0 : _lastFileTime;
                _state = State.Running;
                _capture = OpenCapture(item, source);
                var interval = TimeSpan.FromSeconds(_timeline.FrameInterval);
                _frameTimer = new Timer(_ => OnTick(), null, interval, interval);
                _diskTimer = new Timer(_ => OnDiskCheck(), null, DiskCheckInterval, DiskCheckInterval);
            }
        }
        catch
        {
            Capture? capture;
            lock (_gate)
            {
                capture = Detach();
                StopTimers();
                _device = null;
                _encoder = null;
                _compositor = null;
                _timeline = null;
                _state = State.Idle;
            }
            Close(capture);
            compositor?.Dispose();
            encoder?.Dispose();
            Close(device);
            try
            {
                File.Delete(full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort, as macOS `try? removeItem`.
            }
            throw;
        }
    }

    private void ScreenOff(double? fileTime)
    {
        Capture? capture;
        lock (_gate)
        {
            if (_state != State.Running || _ended || _capture is null) return;
            capture = Detach();
        }
        Close(capture); // no frame of the old capture after this

        Exception? failure = null;
        lock (_gate)
        {
            if (_state != State.Running || _ended) return;
            try
            {
                var time = fileTime ?? FileTimeNow() ?? _lastFileTime ?? 0;
                var step = _timeline!.Placeholder(time);
                if (step.Write is { } sample) WriteHeld(sample);
                if (step.Holds) _heldIsPlaceholder = true;
            }
            catch (Exception e)
            {
                failure = e;
            }
        }
        if (failure is not null) End(ScreenRecorderEnd.WriteFailed, failure);
    }

    private void ScreenOn(ScreenSource source, long requested)
    {
        lock (_gate)
        {
            if (_state != State.Running || _ended) throw new InvalidOperationException("The screen video is not recording");
            if (_capture is not null) throw new InvalidOperationException("The screen is already on");
        }
        var (item, _) = Item(source);
        lock (_gate)
        {
            if (_state != State.Running || _ended || _capture is not null)
            {
                throw new InvalidOperationException("The screen video stopped or turned on meanwhile");
            }
            _anchor = _fileTime(requested);
            if (_anchor is { } anchor) _lastFileTime = anchor;
            _capture = OpenCapture(item, source);
        }
    }

    private void Stop(double? fileTime)
    {
        Capture? capture = null;
        var running = false;
        lock (_gate)
        {
            if (_state == State.Running && !_ended)
            {
                running = true;
                capture = Detach();
                StopTimers();
            }
        }
        if (!running)
        {
            WaitForEnd();
            return;
        }
        Close(capture);

        Exception? failure = null;
        CaptureDevice? device = null;
        var endedMeanwhile = false;
        lock (_gate)
        {
            if (_state != State.Running || _ended)
            {
                endedMeanwhile = true;
            }
            else
            {
                failure = Finish(fileTime ?? FileTimeNow() ?? _lastFileTime ?? 0);
                device = _device;
                _device = null;
                _state = State.Stopped;
            }
        }
        if (endedMeanwhile)
        {
            WaitForEnd();
            return;
        }
        Close(device);
        if (failure is not null) throw failure;
    }

    /// <summary>Ends the video for good, once: closes the capture and finishes the file at the last file time.</summary>
    private void End(ScreenRecorderEnd reason, Exception? error)
    {
        if (error is not null) SetLastError(error);
        Capture? capture;
        lock (_gate)
        {
            if (_state != State.Running || _ended) return;
            _ended = true;
            capture = Detach();
            StopTimers();
        }
        try
        {
            Close(capture);

            CaptureDevice? device;
            lock (_gate)
            {
                Finish(FileTimeNow() ?? _lastFileTime ?? 0);
                device = _device;
                _device = null;
                _state = State.Stopped;
            }
            Close(device);
        }
        finally
        {
            _endFinished.TrySetResult(); // a Stop that found the video ending waits for this
        }
        try
        {
            Ended?.Invoke(reason);
        }
        catch (Exception e)
        {
            SetLastError(e); // a handler's error must not end the process (and the audio)
        }
    }

    // Capture callbacks and timers: never throw (an exception on a pool thread would end the process and the audio).

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, int generation)
    {
        var arrival = Now();
        try
        {
            lock (_gate)
            {
                if (generation != _generation || _capture is not { } capture || _state != State.Running || _ended) return;
                SizeInt32 content;
                var restart = false;
                using (var frame = pool.TryGetNextFrame())
                {
                    if (frame is null) return;
                    var stamp = frame.SystemRelativeTime.Ticks;
                    RecordLatency((arrival - stamp) / (double)TimeSpan.TicksPerMillisecond);
                    content = frame.ContentSize;
                    var first = Arrived(capture, arrival);
                    var crop = Crop(capture.Source, content);
                    if (first || crop is not null)
                    {
                        var texture = FrameCompositor.Texture(frame.Surface);
                        try
                        {
                            if (first) restart = CheckFirstFrame(capture, texture, content);
                            if (!restart && crop is { } area)
                            {
                                _compositor!.Compose(texture, area);
                                _pending = true;
                                var time = _paused ? null : _fileTime(stamp);
                                if (time is not null) _lastFileTime = time;
                                Feed(time);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(texture);
                        }
                    }
                }
                if (restart)
                {
                    // Queued with the operations (never closed here: closing waits for this callback).
                    _ = Serialized(() => Restart(capture));
                    return;
                }
                // A resized window or monitor: frames of the new size from now on (aspect-fitted into the output).
                if (content.Width > 0 && content.Height > 0 && !content.Equals(capture.PoolSize))
                {
                    pool.Recreate(_device!.Direct3DDevice, PixelFormat, 2, content);
                    capture.PoolSize = content;
                }
            }
        }
        catch (Exception e)
        {
            // Not on the pool's own callback: closing the session there could wait for this callback.
            _ = Task.Run(() => End(ScreenRecorderEnd.WriteFailed, e));
        }
    }

    /// <summary>
    /// Replaces <paramref name="black"/>, a monitor or area capture whose first frame was black and not written, by a
    /// new capture of the same source (a new item, pool and session), once: the replacement is never restarted.
    /// </summary>
    /// <remarks>
    /// In a Windows 11 ARM64 VM the first frame of the first monitor capture of a process was sometimes composed solid
    /// black and, the screen being static, Windows.Graphics.Capture sent no other frame, so the whole video stayed black.
    /// A second capture of the same monitor in the same process (screen off, then on) gave a correct first frame. The
    /// cause is not proven; this keeps such a video from staying black, and the session stats tell whether the raw
    /// frame itself was black. A no-op when a later frame was already written or the capture changed meanwhile.
    /// </remarks>
    private void Restart(Capture black)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_capture, black) || _state != State.Running || _ended || !_awaitingFirst) return;
                Detach();
            }
            lock (_statsGate) black.Log.Restarted = true;
            Close(black);
            var (item, _) = Item(black.Source);
            lock (_gate)
            {
                if (_state != State.Running || _ended || _capture is not null) return;
                // _anchor and _awaitingFirst are kept: the replacement's first frame is written at the request's time.
                _capture = OpenCapture(item, black.Source, replacement: true);
            }
        }
        catch (Exception e)
        {
            End(ScreenRecorderEnd.WriteFailed, e);
        }
    }

    private void OnItemClosed(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
        }
        End(ScreenRecorderEnd.StreamStopped, null);
    }

    /// <summary>Every frame interval: feeds a pending frame at the current file time.</summary>
    private void OnTick()
    {
        if (!Monitor.TryEnter(_gate)) return; // busy writing: the next tick or frame does it
        Exception? failure = null;
        try
        {
            if (_state != State.Running || _ended) return;
            var time = FileTimeNow();
            if (time is not null) _lastFileTime = time;
            if (_pending && !_paused) Feed(time);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
        if (failure is null) return;
        try
        {
            End(ScreenRecorderEnd.WriteFailed, failure);
        }
        catch (Exception e)
        {
            SetLastError(e);
        }
    }

    private void OnDiskCheck()
    {
        try
        {
            string folder;
            long minimum;
            lock (_gate)
            {
                if (_state != State.Running || _ended) return;
                folder = _folder;
                minimum = _minimumFreeBytes;
            }
            if (ScreenGeometry.IsLowDisk(FreeSpace(folder), minimum)) End(ScreenRecorderEnd.LowDisk, null);
        }
        catch (Exception e)
        {
            SetLastError(e);
        }
    }

    // Inside _gate

    /// <summary>
    /// Offers the composed pending frame to the timeline at <paramref name="time"/> (null: paused, it stays pending):
    /// writes the held picture when due, and reads the frame back as the new held picture when taken.
    /// </summary>
    private void Feed(double? time)
    {
        if (time is not { } t) return;
        if (_awaitingFirst)
        {
            _awaitingFirst = false;
            if (_anchor is { } anchor) t = anchor;
        }
        var step = _timeline!.Frame(t);
        if (step.Write is { } sample) WriteHeld(sample);
        if (!step.Holds) return;
        _compositor!.ReadPixels(_picture);
        _heldIsPlaceholder = false;
        _pending = false;
    }

    /// <summary>Counts an arrival of <paramref name="capture"/>; true for its first.</summary>
    private bool Arrived(Capture capture, long arrival)
    {
        lock (_statsGate)
        {
            capture.Log.Arrivals++;
            if (capture.Log.FirstArrival is not null) return false;
            capture.Log.FirstArrival = arrival;
            return true;
        }
    }

    /// <summary>
    /// Records the raw statistics of <paramref name="capture"/>'s first frame. True when the frame must not be written
    /// and the capture restarted (<see cref="Restart"/>): black, of a monitor or area, and not a replacement already.
    /// A failure to read the statistics is recorded and never stops the frame.
    /// </summary>
    private bool CheckFirstFrame(Capture capture, Windows.Win32.Graphics.Direct3D11.ID3D11Texture2D texture, SizeInt32 content)
    {
        ScreenFrameSample? sample = null;
        string? error = null;
        try
        {
            sample = _compositor!.Sample(texture, content.Width, content.Height);
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
        }
        lock (_statsGate)
        {
            capture.Log.FirstFrame = sample;
            capture.Log.SampleError = error;
        }
        return sample is { Black: true } && capture.Source.Kind != ScreenSourceKind.Window && !capture.IsReplacement;
    }

    private void WriteHeld(VideoTimeline.Sample sample)
    {
        var size = _compositor!.Size;
        _encoder!.WriteFrame(_heldIsPlaceholder ? _placeholder : _picture, size.Width * 4, sample.Time, sample.Duration);
        lock (_statsGate)
        {
            if (_heldIsPlaceholder) _placeholdersWritten++;
            else _framesWritten++;
        }
    }

    /// <summary>
    /// Pads the held picture to <paramref name="time"/>, finalizes the file and releases the compositor and encoder.
    /// Returns the first error (also kept as LastError), null when the file is complete.
    /// </summary>
    private Exception? Finish(double time)
    {
        Exception? failure = null;
        try
        {
            if (_timeline?.Stop(time) is { } sample) WriteHeld(sample);
        }
        catch (Exception e)
        {
            failure = e;
        }
        try
        {
            _encoder?.Finish();
        }
        catch (Exception e)
        {
            failure ??= e;
        }
        try
        {
            _compositor?.Dispose();
            _encoder?.Dispose();
        }
        catch (Exception e)
        {
            failure ??= e;
        }
        _compositor = null;
        _encoder = null;
        _pending = false;
        if (failure is not null) SetLastError(failure);
        return failure;
    }

    /// <summary>
    /// A capture of <paramref name="item"/>, started; its first frame waits for the request's anchor.
    /// <paramref name="replacement"/>: it replaces a capture whose first frame was black (<see cref="Restart"/>).
    /// </summary>
    private Capture OpenCapture(GraphicsCaptureItem item, ScreenSource source, bool replacement = false)
    {
        var generation = ++_generation;
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device!.Direct3DDevice, PixelFormat, 2, item.Size);
        GraphicsCaptureSession? session = null;
        try
        {
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = _showsCursor;
            var log = new SessionLog(source.Kind);
            var capture = new Capture(item, source, pool, session, item.Size, log, replacement,
                (sender, _) => OnFrameArrived(sender, generation),
                (_, _) => Task.Run(() => OnItemClosed(generation)));
            log.Started = Now();
            session.StartCapture();
            lock (_statsGate) _sessions.Add(log);
            _awaitingFirst = true;
            _pending = false;
            return capture;
        }
        catch
        {
            session?.Dispose();
            pool.Dispose();
            throw;
        }
    }

    /// <summary>Takes the capture out, so its callbacks do nothing; the caller disposes it outside the lock.</summary>
    private Capture? Detach()
    {
        var capture = _capture;
        _capture = null;
        _generation++;
        _pending = false;
        return capture;
    }

    /// <summary>
    /// Once the video ended on its own (<see cref="End"/>, not serialized with the operations, may still be writing),
    /// waits until its file is finished; returns at once otherwise (not started, or stopped).
    /// </summary>
    private void WaitForEnd()
    {
        bool ended;
        lock (_gate) ended = _ended;
        if (ended) _endFinished.Task.Wait();
    }

    private void StopTimers()
    {
        _frameTimer?.Dispose();
        _diskTimer?.Dispose();
        _frameTimer = null;
        _diskTimer = null;
    }

    private double? FileTimeNow() => _fileTime(Now());

    // Teardown is best effort: a failure is kept as LastError and never stops the file from being finished.

    private void Close(Capture? capture) => capture?.Close(SetLastError);

    private void Close(CaptureDevice? device)
    {
        try
        {
            device?.Dispose();
        }
        catch (Exception e)
        {
            SetLastError(e);
        }
    }

    // Helpers

    private async Task Serialized(Action operation)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    private void RecordLatency(double milliseconds)
    {
        lock (_statsGate)
        {
            _latencySamples++;
            if (_latencies.Count < LatencyCapacity) _latencies.Add(milliseconds);
            _latencyMax = Math.Max(_latencyMax, milliseconds);
            _latencyMin = Math.Min(_latencyMin, milliseconds);
        }
    }

    private void SetLastError(Exception e)
    {
        lock (_statsGate) _lastError = $"{e.GetType().Name}: {e.Message}";
    }

    /// <summary>A capture item for <paramref name="source"/> and the size it records, in pixels.</summary>
    private static (GraphicsCaptureItem Item, ScreenGeometry.Size Size) Item(ScreenSource source)
    {
        var item = source.Kind == ScreenSourceKind.Window
            ? GraphicsCapture.ForWindow(source.Handle)
            : GraphicsCapture.ForMonitor(source.Handle);
        var size = new ScreenGeometry.Size(item.Size.Width, item.Size.Height);
        if (source.Kind == ScreenSourceKind.Area)
        {
            var area = ScreenGeometry.AreaPixels(source.Region, size)
                ?? throw new ArgumentException($"The area {source.Region} is not on the monitor ({size.Width}x{size.Height})");
            size = new ScreenGeometry.Size(area.Width, area.Height);
        }
        if (size.Width < 2 || size.Height < 2)
        {
            throw new ArgumentException($"The {source.Kind} source has no size ({size.Width}x{size.Height})");
        }
        return (item, size);
    }

    /// <summary>The part of a frame of <paramref name="content"/> size to record; null when too small (minimized).</summary>
    private static ScreenGeometry.Rect? Crop(ScreenSource source, SizeInt32 content)
    {
        var size = new ScreenGeometry.Size(content.Width, content.Height);
        if (source.Kind == ScreenSourceKind.Area) return ScreenGeometry.AreaPixels(source.Region, size);
        return size.Width >= 2 && size.Height >= 2 ? new ScreenGeometry.Rect(0, 0, size.Width, size.Height) : null;
    }

    /// <summary>QPC now in 100 ns units, the clock of the engine and of <c>SystemRelativeTime</c>.</summary>
    private static long Now() => Stopwatch.GetElapsedTime(0).Ticks;

    /// <summary>The QPC time the process started (now, if its start time cannot be read).</summary>
    private static long ProcessStartTicks()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return Now() - (DateTime.Now - process.StartTime).Ticks;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return Now();
        }
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What a capture session did, for <see cref="ScreenCaptureSession"/>; changed under _statsGate.</summary>
    private sealed class SessionLog(ScreenSourceKind kind)
    {
        public long Started { get; set; } // QPC
        public int Arrivals { get; set; }
        public long? FirstArrival { get; set; } // QPC
        public ScreenFrameSample? FirstFrame { get; set; }
        public string? SampleError { get; set; }
        public bool Restarted { get; set; }

        public ScreenCaptureSession ToRecord() => new(kind,
            (Started - ProcessStart) / (double)TimeSpan.TicksPerMillisecond,
            Arrivals,
            FirstArrival is { } first ? (first - Started) / (double)TimeSpan.TicksPerMillisecond : null,
            FirstFrame, SampleError, Restarted);
    }

    /// <summary>
    /// One capture session: its item, frame pool and session, the size the pool was made for, and its log.
    /// </summary>
    private sealed class Capture
    {
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly TypedEventHandler<Direct3D11CaptureFramePool, object> _frameArrived;
        private readonly TypedEventHandler<GraphicsCaptureItem, object> _closed;

        public Capture(GraphicsCaptureItem item, ScreenSource source, Direct3D11CaptureFramePool pool,
            GraphicsCaptureSession session, SizeInt32 poolSize, SessionLog log, bool isReplacement,
            TypedEventHandler<Direct3D11CaptureFramePool, object> frameArrived,
            TypedEventHandler<GraphicsCaptureItem, object> closed)
        {
            _item = item;
            Log = log;
            IsReplacement = isReplacement;
            _pool = pool;
            _session = session;
            _frameArrived = frameArrived;
            _closed = closed;
            Source = source;
            PoolSize = poolSize;
            pool.FrameArrived += frameArrived;
            item.Closed += closed;
        }

        public ScreenSource Source { get; }

        public SizeInt32 PoolSize { get; set; }

        public SessionLog Log { get; }

        /// <summary>It replaces a capture whose first frame was black: never restarted itself.</summary>
        public bool IsReplacement { get; }

        /// <summary>
        /// Stops the capture, each step even when an earlier one failed (<paramref name="failed"/> gets the errors).
        /// Outside the recorder's lock: closing may wait for a callback blocked on it.
        /// </summary>
        public void Close(Action<Exception> failed)
        {
            Step(() => _item.Closed -= _closed);
            Step(() => _pool.FrameArrived -= _frameArrived);
            Step(_session.Dispose);
            Step(_pool.Dispose);

            void Step(Action step)
            {
                try
                {
                    step();
                }
                catch (Exception e)
                {
                    failed(e);
                }
            }
        }
    }
}
