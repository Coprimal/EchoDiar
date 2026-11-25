using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

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
/// arrival, so the picture starts when the screen was turned on; a start with its recording (<c>startsWithRecording</c>)
/// or within one frame interval of 0 is written at 0, so such a video shows no placeholder at all. Windows.Graphics.Capture only sends a
/// frame when the screen changes, so a frame dropped by <see cref="VideoTimeline"/> (too close to the last one) stays
/// pending, and a timer (every half frame interval) feeds it at the current file time: the newest picture is always
/// written.</para>
/// <para>Overlays (drawn into the video, as macOS ScreenCaptureKit shows the bubble window and the clicks): the camera
/// bubble while <see cref="SetCamera"/> says the camera is on, at its place on screen, and a ring at each click while
/// <c>showsClicks</c> is on; both only on captured pictures (never on the placeholder) and clipped to the captured
/// region. While either shows, the frame timer composes the last captured picture again with them, so the camera moves
/// and the rings fade on a static screen. The click watcher is registered only while the screen is captured, not
/// paused.</para>
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
    private static readonly long RingTicks = TimeSpan.FromSeconds(0.35).Ticks; // how long a click ring shows
    private const int MaxClicks = 32;

    // The click watcher is one per app (Raw Input registration is process-wide): the recorder that registered it last
    // owns it, so a finishing recorder never unregisters a newer one's. Never taken with _gate inside it.
    private static readonly object ClicksGate = new();
    private static ScreenRecorder? _clicksOwner;

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
    private volatile bool _capturing; // a capture is open (_capture is not null), for the click watcher
    private volatile bool _showsClicks;
    private volatile ClickWatcher? _clickWatcher;
    private bool _hasBase; // the compositor keeps a picture of the current capture, to draw overlays on again
    private bool _overlaysDrawn; // the last composed picture has an overlay (bubble or ring)
    private ScreenGeometry.Rect _crop; // the current capture's last picture: its crop, in its content's pixels
    private long _composeTime; // QPC of the picture being composed, for the rings' age
    private byte[]? _cameraPixels;
    private long _cameraNumber; // the camera frame the compositor has; 0: none of the current camera
    private int _cameraSeen; // the _cameraGeneration of _cameraNumber

    // Overlay inputs, set from any thread: under _overlayGate (taken inside _gate, never the other way)
    private readonly object _overlayGate = new();
    private CameraSource? _camera;
    private Func<CameraBubblePlacement?>? _placement;
    private int _cameraGeneration;
    private readonly List<(int X, int Y, long Time)> _clicks = [];

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
    /// <param name="showsClicks">A ring at each click of <paramref name="clickWatcher"/> (none without it).</param>
    /// <param name="minimumFreeBytes">Below this free space of the video folder the start fails with
    /// <see cref="ScreenLowDiskException"/>, and a running video ends with <see cref="ScreenRecorderEnd.LowDisk"/>.</param>
    /// <param name="fileTime">The engine's file time of a QPC time in 100 ns units, null while paused or idle
    /// (<c>RecordingEngine.FileTime</c>); called off the UI thread.</param>
    /// <param name="placeholderText">Already localized ("Screen recording off").</param>
    /// <param name="clickWatcher">The app's one click watcher, registered by the recorder only while the screen is
    /// captured and not paused, with <paramref name="showsClicks"/>.</param>
    /// <param name="startsWithRecording">The video starts with its recording (picked before it started): the first
    /// picture is written at file time 0 whenever the call comes, so a slow audio start (the first mic open of the
    /// process, a consent prompt) never shows as the placeholder. False: from the file time of this call, the
    /// placeholder before it (a video started during the recording, macOS <c>leadsWithPlaceholder</c>).</param>
    /// <exception cref="InvalidOperationException">Started before.</exception>
    /// <exception cref="ScreenLowDiskException">Not enough free space; nothing was created.</exception>
    /// <remarks>On any failure the file is removed and the recorder can be started again.</remarks>
    public Task StartAsync(ScreenSource source, int fps, double quality, bool showsCursor, bool showsClicks, string path,
        long minimumFreeBytes, Func<long, double?> fileTime, string placeholderText, ClickWatcher? clickWatcher = null,
        bool startsWithRecording = false)
    {
        var requested = Now();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentNullException.ThrowIfNull(fileTime);
        return Serialized(() => Start(source, fps, quality, showsCursor, showsClicks, clickWatcher, path, minimumFreeBytes,
            fileTime, placeholderText, requested, startsWithRecording));
    }

    /// <summary>
    /// The camera bubble in the video (macOS: the bubble window is in the captured picture): with
    /// <paramref name="camera"/>, its latest frame in a circle at <paramref name="placement"/>'s square (physical
    /// virtual-screen pixels; null: hidden, not drawn), the grey placeholder until the camera's first frame; null
    /// turns it off, gone from the next picture. Drawn on captured pictures only, in every source, clipped to the
    /// captured region. Any thread, any time (also before <see cref="StartAsync"/>); never throws. The recorder only
    /// copies frames (<see cref="CameraSource.CopyLatest"/>) and calls <paramref name="placement"/> (on its threads,
    /// so it must be quick and thread-safe, as <see cref="CameraBubbleWindow.Placement"/>); it never starts or stops
    /// the camera.
    /// </summary>
    public void SetCamera(CameraSource? camera, Func<CameraBubblePlacement?>? placement)
    {
        lock (_overlayGate)
        {
            _camera = camera;
            _placement = camera is null ? null : placement;
            _cameraGeneration++;
        }
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

    /// <summary>Frames are not written until <see cref="Resume"/>; the capture keeps running, clicks are not watched.</summary>
    public void Pause()
    {
        _paused = true;
        SyncClicks();
    }

    public void Resume()
    {
        _paused = false;
        SyncClicks();
    }

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

    private void Start(ScreenSource source, int fps, double quality, bool showsCursor, bool showsClicks,
        ClickWatcher? clickWatcher, string path, long minimumFreeBytes, Func<long, double?> fileTime,
        string placeholderText, long requested, bool startsWithRecording)
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
            compositor.Overlay = DrawOverlays; // after the placeholder: it never has overlays

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
                // A video started with its recording, or within one frame interval of 0, starts the picture at 0.
                _anchor = startsWithRecording || _lastFileTime < _timeline.FrameInterval ? 0 : _lastFileTime;
                _state = State.Running;
                if (showsClicks && clickWatcher is not null)
                {
                    _clickWatcher = clickWatcher;
                    _showsClicks = true;
                    clickWatcher.Clicked += OnClicked;
                }
                _capture = OpenCapture(item, source);
                // Every half frame interval: the system timer fires a little early or late, and a picture earlier than
                // one interval after the last is dropped by the timeline: a 1/fps timer would lose about every other tick.
                var interval = TimeSpan.FromSeconds(_timeline.FrameInterval / 2);
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
                ReleaseClicks();
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
                // Its first frame was black and its replacement is queued (Restart): a later frame of it must not take
                // the first frame's place (the anchor) nor cancel the restart.
                if (capture.Replacing) return;
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
                                _crop = area;
                                _composeTime = stamp;
                                _compositor!.Compose(texture, area);
                                _hasBase = true;
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
                    capture.Replacing = true;
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
    /// frame itself was black. Frames of <paramref name="black"/> after its first are ignored (<c>Replacing</c>). A no-op
    /// when the capture changed meanwhile.
    /// </remarks>
    private void Restart(Capture black)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_capture, black) || _state != State.Running || _ended) return;
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

    /// <summary>
    /// Every half frame interval: composes the last captured picture again while an overlay shows or has just gone (the
    /// camera moves, rings fade, without a new capture frame), and feeds a pending frame at the current file time.
    /// </summary>
    private void OnTick()
    {
        if (!Monitor.TryEnter(_gate)) return; // busy writing: the next tick or frame does it
        Exception? failure = null;
        try
        {
            if (_state != State.Running || _ended) return;
            var now = Now();
            var time = _fileTime(now);
            if (time is not null) _lastFileTime = time;
            if (!_paused && _hasBase && _capture is not null && (_overlaysDrawn || OverlaysShow(now)))
            {
                _composeTime = now;
                if (_compositor!.Recompose()) _pending = true;
            }
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
        ReleaseClicks();
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
            _hasBase = false;
            _overlaysDrawn = false;
            _capturing = true;
            SyncClicks();
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
        _hasBase = false;
        _overlaysDrawn = false;
        _capturing = false;
        SyncClicks();
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

    // Overlays (inside _gate unless said)

    /// <summary>The camera bubble shows (camera on, bubble placed) or a ring of a click is still fading at <paramref name="now"/>.</summary>
    private bool OverlaysShow(long now)
    {
        Func<CameraBubblePlacement?>? placement;
        lock (_overlayGate)
        {
            foreach (var click in _clicks)
            {
                if (now - click.Time < RingTicks) return true;
            }
            placement = _placement;
        }
        return Placement(placement) is not null;
    }

    /// <summary>
    /// <see cref="FrameCompositor.Overlay"/>: the camera bubble and the click rings on the picture being composed,
    /// mapped from virtual-screen pixels into the output through the region the capture shows and its aspect fit,
    /// clipped to it. Its failures are kept as LastError and never stop the picture.
    /// </summary>
    private void DrawOverlays(ID2D1DeviceContext context, ScreenGeometry.Size size)
    {
        _overlaysDrawn = false;
        if (_capture is not { } capture || _compositor is not { } compositor) return;
        CameraSource? camera;
        Func<CameraBubblePlacement?>? placement;
        int cameraGeneration;
        List<(int X, int Y, long Time)>? rings = null;
        lock (_overlayGate)
        {
            (camera, placement, cameraGeneration) = (_camera, _placement, _cameraGeneration);
            foreach (var click in _clicks)
            {
                var age = _composeTime - click.Time;
                if (age >= 0 && age < RingTicks) (rings ??= []).Add(click);
            }
        }
        var bubble = Placement(placement);
        if (bubble is null && rings is null) return;
        _overlaysDrawn = true;
        if (Region(capture) is not { } region || region.Width <= 0 || region.Height <= 0) return;

        var fit = ScreenGeometry.AspectFit(new ScreenGeometry.Size(_crop.Width, _crop.Height), size);
        double scaleX = fit.Width / (double)region.Width, scaleY = fit.Height / (double)region.Height;
        var clip = new Windows.Win32.Graphics.Direct2D.Common.D2D_RECT_F
        {
            left = fit.X, top = fit.Y, right = fit.X + fit.Width, bottom = fit.Y + fit.Height,
        };
        context.PushAxisAlignedClip(clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        try
        {
            if (bubble is { } place)
            {
                try
                {
                    if (cameraGeneration != _cameraSeen)
                    {
                        _cameraSeen = cameraGeneration;
                        _cameraNumber = 0; // another camera, or on again: the placeholder until its next frame
                    }
                    if (camera?.CopyLatest(ref _cameraPixels, _cameraNumber) is { } frame)
                    {
                        compositor.SetCameraFrame(_cameraPixels, frame.Width, frame.Height, frame.Stride);
                        _cameraNumber = frame.Number;
                    }
                    var bounds = place.Bounds;
                    compositor.DrawBubble(new ScreenGeometry.Rect(
                        fit.X + (int)Math.Round((bounds.X - region.X) * scaleX),
                        fit.Y + (int)Math.Round((bounds.Y - region.Y) * scaleY),
                        (int)Math.Round(bounds.Width * scaleX), (int)Math.Round(bounds.Height * scaleY)), _cameraNumber != 0);
                }
                catch (Exception e)
                {
                    SetLastError(e); // no bubble in this picture
                }
            }
            if (rings is not null)
            {
                // Output-relative: the same size in every source and output (about 10 to 25 px at 840 px high).
                float small = size.Height * 0.012f, large = size.Height * 0.03f, stroke = Math.Max(2, size.Height * 0.004f);
                foreach (var ring in rings)
                {
                    var progress = (float)((_composeTime - ring.Time) / (double)RingTicks);
                    var grown = 1 - (1 - progress) * (1 - progress); // ease out
                    compositor.DrawRing((float)(fit.X + (ring.X - region.X) * scaleX), (float)(fit.Y + (ring.Y - region.Y) * scaleY),
                        small + (large - small) * grown, stroke, 1 - progress);
                }
            }
        }
        finally
        {
            context.PopAxisAlignedClip();
        }
    }

    /// <summary>The bubble's place, null when off or hidden (or the callback failed: kept as LastError).</summary>
    private CameraBubblePlacement? Placement(Func<CameraBubblePlacement?>? placement)
    {
        if (placement is null) return null;
        try
        {
            return placement();
        }
        catch (Exception e)
        {
            SetLastError(e);
            return null;
        }
    }

    /// <summary>
    /// The virtual-screen rectangle (physical pixels) the current picture shows: the monitor, the area on it, or the
    /// window's frame now (DWM extended frame bounds, else its window rectangle). Null when it cannot be read.
    /// </summary>
    private unsafe ScreenGeometry.Rect? Region(Capture capture)
    {
        // Physical pixels whatever the process's DPI awareness, as the bubble's and the clicks'.
        var previous = PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var source = capture.Source;
            if (source.Kind == ScreenSourceKind.Window)
            {
                var window = (HWND)source.Handle;
                RECT frame;
                if (PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Failed
                    && !PInvoke.GetWindowRect(window, out frame))
                {
                    return null;
                }
                return new ScreenGeometry.Rect(frame.left, frame.top, frame.right - frame.left, frame.bottom - frame.top);
            }
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!PInvoke.GetMonitorInfo((HMONITOR)source.Handle, &info)) return null;
            var monitor = info.rcMonitor;
            return source.Kind == ScreenSourceKind.Area
                ? new ScreenGeometry.Rect(monitor.left + _crop.X, monitor.top + _crop.Y, _crop.Width, _crop.Height)
                : new ScreenGeometry.Rect(monitor.left, monitor.top, monitor.right - monitor.left, monitor.bottom - monitor.top);
        }
        finally
        {
            if (!previous.IsNull) PInvoke.SetThreadDpiAwarenessContext(previous);
        }
    }

    // Clicks (any thread)

    /// <summary><see cref="ClickWatcher.Clicked"/>, on the watcher's thread: a ring from <paramref name="time"/>.</summary>
    private void OnClicked(int x, int y, ClickButton button, long time)
    {
        _ = button; // every button has the same ring
        if (!_capturing || _paused) return;
        lock (_overlayGate)
        {
            _clicks.RemoveAll(click => time - click.Time >= RingTicks);
            if (_clicks.Count >= MaxClicks) _clicks.RemoveAt(0);
            _clicks.Add((x, y, time));
        }
    }

    /// <summary>
    /// Registers the click watcher while the screen is captured, not paused, with rings on; else unregisters it when
    /// this recorder registered it last. Its Start and Stop never block nor throw.
    /// </summary>
    private void SyncClicks()
    {
        if (_clickWatcher is not { } watcher) return;
        lock (ClicksGate)
        {
            if (_showsClicks && _capturing && !_paused)
            {
                _clicksOwner = this;
                watcher.Start();
            }
            else if (ReferenceEquals(_clicksOwner, this))
            {
                _clicksOwner = null;
                watcher.Stop();
            }
        }
    }

    /// <summary>The video is over: no more rings, the watcher unregistered (when this recorder owns it).</summary>
    private void ReleaseClicks()
    {
        _showsClicks = false;
        SyncClicks();
        if (_clickWatcher is { } watcher) watcher.Clicked -= OnClicked;
        lock (_overlayGate) _clicks.Clear();
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

        /// <summary>Its first frame was black and its replacement is queued: its frames are ignored. Under the lock.</summary>
        public bool Replacing { get; set; }

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
