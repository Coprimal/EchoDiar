using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Screen;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Screen;

namespace EchoDiar.App.Services;

/// <summary>
/// The screen video of the current recording (macOS ScreenRecordingController.swift): the source picker, the video's
/// state, its <see cref="ScreenRecorder"/>, the outline around a Portion, the camera bubble and the click watcher. The
/// view model calls it when the recording starts, pauses, resumes and ends; a video that ends early never stops the
/// audio. The screen can be turned off and on any number of times in a recording: one recorder writes one
/// <see cref="VideoFileName"/>, with the "Screen recording off" frame where the screen was off. UI thread only: the
/// picker's and the camera's results come back through an await on it, the recorder's end and the camera's failure are
/// posted to it, and the recorder and the camera work on the thread pool.
/// </summary>
internal sealed class ScreenRecordingController
{
    /// <summary>Why the video ended before the recording.</summary>
    public enum EndReason { StartFailed, StreamStopped, WriteFailed, LowDisk, ResumeFailed }

    public enum ScreenState { Off, Starting, Recording, Paused, Ended }

    /// <summary>The video's file in the recording's folder (macOS <c>ScreenSource.videoFileName</c>).</summary>
    public const string VideoFileName = "video.mp4";

    private readonly RecordingEngine _engine;
    private readonly Action<Action> _post;

    /// <summary>
    /// The current recording's video, from <see cref="Start"/> until <see cref="Finish"/> or until it ends early
    /// (<see cref="End"/>); kept while the screen is off.
    /// </summary>
    private ScreenRecorder? _recorder;

    /// <summary>The video that ended early in the current recording, until the recording ends: it may still be added to <c>session.json</c>.</summary>
    private ScreenRecorder? _endedRecorder;

    /// <summary>The finishing of the video that ended early, handed to the recording's end.</summary>
    private Task? _stopping;

    /// <summary>
    /// The finishing of every video that ended (early or with its recording) and may still be writing its file, which
    /// the app's quit waits for (<see cref="Shutdown"/>); never faults.
    /// </summary>
    private Task? _finishing;

    /// <summary>What the screen records, from a start or turn-on until the screen is turned off or the video ends.</summary>
    private ScreenSource? _source;

    /// <summary>EchoDiar's outline around a Portion while it records (not paused).</summary>
    private RecordingOutline? _outline;

    /// <summary>The open picker's cancellation; null while none is open (one at a time).</summary>
    private CancellationTokenSource? _picking;

    /// <summary>The recording is paused (asked while the video was starting or running).</summary>
    private bool _paused;

    private EndReason? _endReason;

    /// <summary>The recording was marked "screen only" when it started.</summary>
    private bool _skipsTranscription;

    /// <summary>Bumped by every start, turn-on, turn-off and end: a late start or turn-on never changes a newer state.</summary>
    private int _generation;

    /// <summary>The App's probes folder, where a DEBUG build's camera looks for its <c>fake-camera</c> marker.</summary>
    private readonly string _probes;

    /// <summary>
    /// The camera and its bubble, made together on first use (nothing is created while "Camera bubble" stays off) and
    /// kept until <see cref="Shutdown"/>.
    /// </summary>
    private CameraSource? _camera;
    private CameraBubbleWindow? _bubble;

    /// <summary>
    /// The app's one click watcher (Raw Input registration is process-wide), made on the first video with "Highlight
    /// clicks" on; each recorder registers it only while it captures.
    /// </summary>
    private ClickWatcher? _clicks;

    /// <summary>
    /// "Camera bubble" for the current video (macOS <c>showsCamera</c>): read at each start and turn-on, changed by
    /// <see cref="SetCameraOption"/>; off for the video when its camera could not open.
    /// </summary>
    private bool _showsCamera;

    /// <summary>
    /// "Blur background" (macOS <c>screenCameraBlur</c>): read at each start and turn-on, changed by
    /// <see cref="SetCameraBlur"/>, given to the camera before it opens and while it runs.
    /// </summary>
    private bool _cameraBlur;

    /// <summary>The bubble shows and its camera is open or opening.</summary>
    private bool _cameraOn;

    /// <summary>The source the bubble was shown for while <see cref="_cameraOn"/>.</summary>
    private ScreenSource? _cameraSource;

    /// <summary>The recorder drawing the camera into its video, from the camera's open until the bubble hides.</summary>
    private ScreenRecorder? _cameraRecorder;

    /// <summary>Bumped by every camera on and off: a late open never changes a newer state.</summary>
    private int _cameraGeneration;

    /// <summary>The camera's opens and stops, one at a time in call order on the thread pool (a stop can block).</summary>
    private Task _cameraWork = Task.CompletedTask;

    /// <summary><see cref="Shutdown"/> ran: no camera or watcher is made again.</summary>
    private bool _shutDown;

    // The camera's frame copied for the bubble, under _frameGate (camera threads).
    private readonly object _frameGate = new();
    private byte[]? _frame;
    private long _frameNumber;

    /// <param name="probesFolder">The App's probes folder (<c>AppPaths.Probes</c>), for the DEBUG fake camera's marker.</param>
    /// <param name="post">Runs an action on the UI thread (the recorder's end arrives on a background thread).</param>
    public ScreenRecordingController(RecordingEngine engine, string probesFolder, Action<Action> post)
    {
        _engine = engine;
        _probes = probesFolder;
        _post = post;
    }

    /// <summary><see cref="State"/>, <see cref="IsOn"/>, <see cref="CanStartDuringRecording"/> or <see cref="Notice"/> may have changed. UI thread.</summary>
    public event Action? Changed;

    public ScreenState State { get; private set; }

    /// <summary>Shown on the recording when its video ended early or the screen could not be turned on; null = nothing to say.</summary>
    public string? Notice { get; private set; }

    /// <summary>A video is starting or running in the current recording (paused with it included).</summary>
    public bool IsOn => State is ScreenState.Starting or ScreenState.Recording or ScreenState.Paused;

    /// <summary>
    /// The screen can be turned on in the current recording: it is off, and the video has not ended on an error
    /// (<see cref="ScreenState.Ended"/> stays until the recording ends).
    /// </summary>
    public bool CanStartDuringRecording => State == ScreenState.Off;

    /// <summary>The current video (null without one, or once it ended early), paused and stopped with the audio.</summary>
    public ScreenRecorder? Recorder => _recorder;

    /// <summary>
    /// Shows the source picker and completes on the caller's (UI) thread with the picked source; null on Esc or any
    /// cancel, while another picker is open, or once <see cref="CancelPicker"/> closed it. <paramref name="duringRecording"/>:
    /// for the running recording (its caption says so), else for a new one.
    /// </summary>
    public async Task<ScreenSource?> PickAsync(bool duringRecording)
    {
        if (_picking is not null) return null;
        var picking = new CancellationTokenSource();
        _picking = picking;
        var texts = new SourcePickerTexts(
            Loc.T("Entire screen"), Loc.T("Window"), Loc.T("Portion of the screen"),
            duringRecording ? Loc.T("Records it in this recording from now on.") : Loc.T("The recording starts as soon as you pick."),
            Loc.T("Press Esc to cancel"), Loc.T("Click to record this display"));
        try
        {
            // Completes on the picker's own thread; the await resumes on this one.
            return await SourcePicker.PickAsync(SourcePickerMode.Display, texts, picking.Token, Log);
        }
        finally
        {
            _picking = null;
            picking.Dispose();
        }
    }

    /// <summary>Closes the open picker, which then picks nothing (a recording starts or ends meanwhile, the app quits).</summary>
    public void CancelPicker() => _picking?.Cancel();

    /// <summary>
    /// Starts the video of the recording the engine runs in <paramref name="folder"/>, on the audio file's clock, with
    /// the screen settings read now (macOS <c>start</c>); the recording's <c>session.json</c> gets the video once it has
    /// started. <paramref name="withRecording"/>: picked before the recording started, so the picture starts at file
    /// time 0 however long the engine's start took (its first mic open runs after the audio clock started); else
    /// (started during the recording) the file starts with the "Screen recording off" frame up to now. A start that
    /// fails ends the video for this recording with a notice; the sound goes on. No-op when a video already runs or the
    /// engine stopped meanwhile.
    /// </summary>
    public void Start(ScreenSource source, string folder, AppSettings settings, bool skipsTranscription, bool withRecording = true)
    {
        if (_recorder is not null || _engine.CurrentSnapshot().State == RecordingEngine.State.Idle) return;
        var recorder = new ScreenRecorder();
        _recorder = recorder;
        _paused = false;
        _endReason = null;
        _skipsTranscription = skipsTranscription;
        _showsCamera = settings.ScreenCamera;
        _cameraBlur = settings.ScreenCameraBlur;
        recorder.Ended += reason => _post(() => OnEnded(recorder, reason));
        var generation = Prepare(source);
        var video = new SessionVideo
        {
            FileName = VideoFileName,
            Source = source.Kind switch
            {
                ScreenSourceKind.Window => VideoSource.Window,
                ScreenSourceKind.Area => VideoSource.Area,
                _ => VideoSource.Display,
            },
            Fps = settings.ScreenFPS,
            Quality = settings.ScreenQuality,
            Width = 0,
            Height = 0,
        };
        _ = StartVideoAsync(recorder, source, generation, Path.Combine(folder, VideoFileName), settings, video, withRecording);
    }

    /// <summary>
    /// A source picked during the recording the engine runs in <paramref name="folder"/> (not paused), on the audio
    /// file's clock (macOS <c>startDuringRecording</c>). Without a video yet, starts one (<see cref="Start"/>); with the
    /// screen off, turns it on again in the same file, with any source. A turn-on that fails leaves the screen off with a
    /// notice, and the file goes on. No-op unless <see cref="CanStartDuringRecording"/>.
    /// </summary>
    public void StartDuringRecording(ScreenSource source, string folder, AppSettings settings, bool skipsTranscription)
    {
        if (!CanStartDuringRecording) return;
        if (_recorder is not { } recorder)
        {
            Start(source, folder, settings, skipsTranscription, withRecording: false);
            return;
        }
        _showsCamera = settings.ScreenCamera;
        _cameraBlur = settings.ScreenCameraBlur;
        _ = TurnOnAsync(recorder, source, Prepare(source));
    }

    /// <summary>
    /// "Camera bubble" changed (the toolbar's camera button, the Record options or Settings): applies it to the running
    /// video (macOS <c>setOverlays</c>); the next start or turn-on reads the setting again.
    /// </summary>
    public void SetCameraOption(bool on)
    {
        _showsCamera = on;
        SyncCamera();
    }

    /// <summary>
    /// "Blur background" changed (Settings): the running camera switches at once (macOS <c>setBlur</c>). Turning it on
    /// takes the sharp picture out of the bubble and the video until the first blurred frame; turning it off brings the
    /// sharp frames back and drops the blur-failed notice.
    /// </summary>
    public void SetCameraBlur(bool on)
    {
        _cameraBlur = on;
        if (!on) ClearBlurNotice();
        if (_camera is not { } camera || _bubble is not { } bubble) return;
        camera.SetBlur(on);
        if (on) ClearCameraPicture(camera, bubble);
    }

    /// <summary>
    /// The screen toggle switched off during the recording: the recorder stops capturing and holds the "Screen recording
    /// off" frame from the audio file's current time (the last one seen while paused) until the screen is turned on
    /// again or the recording ends. No-op unless the screen is on.
    /// </summary>
    public void ScreenOff()
    {
        if (!IsOn || _recorder is not { } recorder) return;
        var requested = Now();
        HideScreen();
        _ = ScreenOffAsync(recorder, requested);
    }

    /// <summary>Before the engine pauses: frames are not written until <see cref="Resume"/>; the outline hides.</summary>
    public void Pause()
    {
        if (_recorder is not { } recorder) return;
        _paused = true;
        recorder.Pause();
        if (State == ScreenState.Recording) State = ScreenState.Paused;
        SyncOverlays();
        Changed?.Invoke();
    }

    /// <summary>After the engine resumed.</summary>
    public void Resume()
    {
        if (_recorder is not { } recorder || !_paused) return;
        _paused = false;
        recorder.Resume();
        if (State == ScreenState.Paused) State = ScreenState.Recording;
        SyncOverlays();
        Changed?.Invoke();
    }

    /// <summary>
    /// The recording ended: detaches the current video at once (a next recording starts its own) and finishes its file
    /// at <paramref name="fileTime"/>, so it ends with the audio (macOS <c>finish(at:)</c>). The returned task completes
    /// once the file is finished, also when the video ended early (its own finishing); null when the recording had no
    /// video. Never faults.
    /// </summary>
    public Task? Finish(double fileTime)
    {
        CancelPicker();
        _paused = false;
        _endReason = null;
        Notice = null;
        State = ScreenState.Off;
        _source = null;
        _generation++;
        SyncOverlays();
        _endedRecorder = null;
        var finishing = _stopping;
        _stopping = null;
        if (_recorder is { } recorder)
        {
            _recorder = null;
            finishing = CloseAsync(recorder, fileTime);
            Track(finishing);
        }
        Changed?.Invoke();
        return finishing;
    }

    /// <summary>
    /// The app quits: the picker and the outline close. Returns the current video, which the caller stops with the
    /// audio, and the finishing of the videos that ended (early, or with a recording that just ended) and may still be
    /// writing their files; it never faults.
    /// </summary>
    public (ScreenRecorder? Video, Task? Ended) Shutdown()
    {
        CancelPicker();
        _outline?.Dispose();
        _outline = null;
        _shutDown = true;
        CameraOff();
        _bubble?.Dispose();
        _bubble = null;
        // The video still running stops with the audio without rings (its watcher calls never throw once disposed).
        _clicks?.Dispose();
        _clicks = null;
        if (_camera is { } camera)
        {
            _camera = null;
            // Released after the stops queued before it; the quit waits for it with the videos.
            Track(_cameraWork.ContinueWith(_ =>
            {
                try
                {
                    camera.Dispose();
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not release the camera: {e}");
                }
            }, TaskScheduler.Default));
        }
        return (_recorder, _finishing);
    }

    // Private

    private async Task StartVideoAsync(ScreenRecorder recorder, ScreenSource source, int generation, string path,
        AppSettings settings, SessionVideo video, bool withRecording)
    {
        try
        {
            await recorder.StartAsync(source, settings.ScreenFPS, settings.ScreenQuality, settings.ScreenShowsCursor,
                settings.ScreenShowsClicks, path, RecordingEngine.LowDiskBytes, _engine.FileTime, Loc.T("Screen recording off"),
                settings.ScreenShowsClicks ? Clicks() : null, startsWithRecording: withRecording);
        }
        catch (Exception e)
        {
            // A Finish during the start took the recorder: nothing to say. Low disk at the start is a failed start, as on macOS.
            if (!ReferenceEquals(_recorder, recorder)) return;
            Debug.WriteLine($"EchoDiar: could not start the screen recording: {e}");
            End(EndReason.StartFailed);
            return;
        }
        // A video that ended early meanwhile still has its frames in this recording's file.
        if (ReferenceEquals(_recorder, recorder) || ReferenceEquals(_endedRecorder, recorder))
        {
            var stats = recorder.Stats;
            var announced = video with { Width = stats.Width, Height = stats.Height };
            _ = Task.Run(() =>
            {
                try
                {
                    _engine.SetVideo(announced);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not add the screen video to the session: {e}");
                }
            });
        }
        if (ReferenceEquals(_recorder, recorder)) Started(generation);
    }

    private async Task TurnOnAsync(ScreenRecorder recorder, ScreenSource source, int generation)
    {
        try
        {
            await recorder.ScreenOnAsync(source);
        }
        catch (Exception e)
        {
            // A Finish, an end or a turn-off during the turn-on: nothing to say.
            if (!ReferenceEquals(_recorder, recorder) || generation != _generation) return;
            HideScreen();
            if (_paused) return;
            Debug.WriteLine($"EchoDiar: could not turn the screen recording on: {e}");
            Notice = NoticeText(EndReason.StartFailed, _skipsTranscription);
            Changed?.Invoke();
            return;
        }
        if (ReferenceEquals(_recorder, recorder)) Started(generation);
    }

    /// <summary>The screen is about to record <paramref name="source"/>: its state and outline. Returns the new generation.</summary>
    private int Prepare(ScreenSource source)
    {
        Notice = null;
        State = ScreenState.Starting;
        _source = source;
        _generation++;
        SyncOverlays();
        Changed?.Invoke();
        return _generation;
    }

    /// <summary>The start or turn-on of <paramref name="generation"/> succeeded: the screen records, unless turned off or ended meanwhile.</summary>
    private void Started(int generation)
    {
        if (generation != _generation || State != ScreenState.Starting) return;
        State = _paused ? ScreenState.Paused : ScreenState.Recording;
        SyncOverlays();
        Changed?.Invoke();
    }

    /// <summary>The screen is off with the recorder kept: no source, no outline.</summary>
    private void HideScreen()
    {
        State = ScreenState.Off;
        _source = null;
        _generation++;
        SyncOverlays();
        Changed?.Invoke();
    }

    /// <summary>The outline and the camera bubble follow the screen's state (macOS <c>syncOverlays</c>).</summary>
    private void SyncOverlays()
    {
        SyncOutline();
        SyncCamera();
    }

    /// <summary>The outline shows around a Portion while its video starts or records, not while paused (macOS <c>syncOverlays</c>).</summary>
    private void SyncOutline()
    {
        var shows = !_paused && State is ScreenState.Starting or ScreenState.Recording
            && _source is { Kind: ScreenSourceKind.Area };
        if (!shows)
        {
            _outline?.Dispose();
            _outline = null;
        }
        else if (_outline is null && _source is { } source)
        {
            _outline = RecordingOutline.Show(source.Handle, source.Region, Log);
        }
    }

    /// <summary>
    /// The camera bubble shows with "Camera bubble" on while the video starts or records, not while paused (macOS
    /// <c>syncOverlays</c>): on the recorded display, in a Portion's area, or on a window's monitor (read once per
    /// source; the bubble stays where it is when the window moves). The camera opens off the UI thread and the bubble
    /// shows its placeholder until the first frame; the recorder draws the camera from its open. Off in every other
    /// state: the bubble hides, the video stops drawing it and the camera is released.
    /// </summary>
    private void SyncCamera()
    {
        if (!_showsCamera || _paused || State is not (ScreenState.Starting or ScreenState.Recording)
            || _source is not { } source || _recorder is not { } recorder || _shutDown)
        {
            CameraOff();
            return;
        }
        if (_cameraOn && _cameraSource == source) return;
        var (camera, bubble) = Camera();
        _cameraSource = source;
        switch (source.Kind)
        {
            case ScreenSourceKind.Area:
                bubble.Show(source.Handle, source.Region);
                break;
            case ScreenSourceKind.Window:
                bubble.Show(MonitorFromWindow(source.Handle, MonitorDefaultToNearest));
                break;
            default:
                bubble.Show(source.Handle);
                break;
        }
        if (_cameraOn) return;
        _cameraOn = true;
        // Before the open, so a sharp frame is never delivered with blur on.
        camera.SetBlur(_cameraBlur);
        var generation = ++_cameraGeneration;
        var opening = _cameraWork.ContinueWith(_ => camera.StartAsync(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _cameraWork = opening;
        _ = OpenCameraAsync(opening, camera, bubble, recorder, generation);
    }

    /// <summary>
    /// The camera's open came back: the recorder draws it (with the bubble's placement), unless the bubble was turned off
    /// meanwhile. A camera that could not open turns the bubble off for this video, with a notice when access is off.
    /// </summary>
    private async Task OpenCameraAsync(Task<CameraOpenResult> opening, CameraSource camera, CameraBubbleWindow bubble,
        ScreenRecorder recorder, int generation)
    {
        CameraOpenResult result;
        try
        {
            result = await opening;
        }
        catch (Exception e)
        {
            result = new CameraOpenResult(CameraOpenStatus.Failed, $"{e.GetType().Name}: {e.Message}");
        }
        // Turned off meanwhile: its stop is queued after this open.
        if (generation != _cameraGeneration) return;
        if (result.Status == CameraOpenStatus.Started)
        {
            if (result.IsFake) Log("camera bubble: the fake camera");
            _cameraRecorder = recorder;
            recorder.SetCamera(camera, () => bubble.Placement);
            // Access turned on since an earlier try: the notice no longer applies.
            if (Notice == CameraNotice)
            {
                Notice = null;
                Changed?.Invoke();
            }
            return;
        }
        Log($"camera bubble: the camera did not open ({result.Status}) {result.Message}");
        _showsCamera = false;
        CameraOff();
        if (result.Status != CameraOpenStatus.AccessDenied) return;
        Notice = CameraNotice;
        Changed?.Invoke();
    }

    private static string CameraNotice =>
        Loc.T("Camera access is off. Turn on EchoDiar in Windows Settings › Privacy & security › Camera.");

    private static string BlurNotice => Loc.T("Background blur is not available. The camera bubble shows no picture.");

    /// <summary>
    /// The running camera cannot blur (no model, ONNX Runtime missing, the model failed): it delivers no frame while
    /// blur stays on, so the bubble and the video show the placeholder, with a notice.
    /// </summary>
    private void OnBlurFailed(CameraSource camera, string message)
    {
        if (!_cameraOn || !_cameraBlur || !ReferenceEquals(_camera, camera) || _bubble is not { } bubble) return;
        Log($"camera bubble: no background blur: {message}");
        ClearCameraPicture(camera, bubble);
        Notice = BlurNotice;
        Changed?.Invoke();
    }

    /// <summary>
    /// The bubble and the video show the placeholder until the camera's next frame. Call after the camera dropped its
    /// latest frame (<see cref="CameraSource.SetBlur"/>): a frame the bubble is being given meanwhile lands first,
    /// under <c>_frameGate</c>, and is cleared; the recorder starts again from its placeholder (a new camera generation).
    /// </summary>
    private void ClearCameraPicture(CameraSource camera, CameraBubbleWindow bubble)
    {
        lock (_frameGate) bubble.ClearFrame();
        _cameraRecorder?.SetCamera(camera, () => bubble.Placement);
    }

    private void ClearBlurNotice()
    {
        if (Notice != BlurNotice) return;
        Notice = null;
        Changed?.Invoke();
    }

    /// <summary>The running camera stopped by itself (unplugged, taken by another app): the bubble is off for this video.</summary>
    private void OnCameraFailed(CameraSource camera, string message)
    {
        // A failure of a camera stopped before (its session is gone) and opened again meanwhile changes nothing.
        if (!_cameraOn || !ReferenceEquals(_camera, camera) || camera.IsRunning) return;
        Log($"camera bubble: the camera stopped: {message}");
        _showsCamera = false;
        CameraOff();
    }

    /// <summary>Hides the bubble, takes the camera out of the video and releases the camera (off the UI thread).</summary>
    private void CameraOff()
    {
        if (!_cameraOn) return;
        ClearBlurNotice();
        _cameraOn = false;
        _cameraSource = null;
        _cameraGeneration++;
        _bubble?.Hide();
        _cameraRecorder?.SetCamera(null, null);
        _cameraRecorder = null;
        if (_camera is { } camera) _cameraWork = _cameraWork.ContinueWith(_ => camera.Stop(), TaskScheduler.Default);
    }

    /// <summary>The camera and its bubble, made together on first use; their frames go from one to the other.</summary>
    private (CameraSource Camera, CameraBubbleWindow Bubble) Camera()
    {
        if (_camera is { } made && _bubble is { } shown) return (made, shown);
        var camera = new CameraSource(_probes);
        var bubble = new CameraBubbleWindow(Log);
        camera.FrameAvailable += () => ShowFrame(camera, bubble);
        camera.Failed += message => _post(() => OnCameraFailed(camera, message));
        camera.BlurFailed += message => _post(() => OnBlurFailed(camera, message));
        _camera = camera;
        _bubble = bubble;
        return (camera, bubble);
    }

    /// <summary><see cref="CameraSource.FrameAvailable"/>, on a camera thread: the newest frame to the bubble.</summary>
    private void ShowFrame(CameraSource camera, CameraBubbleWindow bubble)
    {
        lock (_frameGate)
        {
            if (camera.CopyLatest(ref _frame, _frameNumber) is not { } info || _frame is not { } frame) return;
            _frameNumber = info.Number;
            bubble.SetFrame(frame, info.Width, info.Height, info.Stride);
        }
    }

    /// <summary>The app's one click watcher, made on first use; null once the app quits.</summary>
    private ClickWatcher? Clicks() => _shutDown ? null : _clicks ??= new ClickWatcher(Log);

    private void OnEnded(ScreenRecorder recorder, ScreenRecorderEnd reason)
    {
        if (!ReferenceEquals(_recorder, recorder)) return;
        End(reason switch
        {
            ScreenRecorderEnd.StreamStopped => EndReason.StreamStopped,
            ScreenRecorderEnd.LowDisk => EndReason.LowDisk,
            ScreenRecorderEnd.ResumeFailed => EndReason.ResumeFailed,
            _ => EndReason.WriteFailed,
        });
    }

    /// <summary>
    /// The video ended on its own (or could not start): its file is finished at the audio file's current time, the
    /// sound goes on, and the recording's end waits for the file (<see cref="Finish"/>).
    /// </summary>
    private void End(EndReason reason)
    {
        if (_endReason is not null || _recorder is not { } recorder) return;
        _endReason = reason;
        State = ScreenState.Ended;
        Notice = NoticeText(reason, _skipsTranscription);
        _source = null;
        _generation++;
        SyncOverlays();
        _recorder = null;
        _endedRecorder = recorder;
        // Its file is already finished (or was never created): nothing to pad.
        _stopping = CloseAsync(recorder, null);
        Track(_stopping);
        Changed?.Invoke();
    }

    /// <summary>Adds <paramref name="finishing"/> to what the quit waits for; one already done is dropped.</summary>
    private void Track(Task finishing) =>
        _finishing = _finishing is null || _finishing.IsCompleted ? finishing : Task.WhenAll(_finishing, finishing);

    /// <summary>Finishes <paramref name="recorder"/>'s file at <paramref name="fileTime"/> (null: its last file time); never faults.</summary>
    private static async Task CloseAsync(ScreenRecorder recorder, double? fileTime)
    {
        try
        {
            await recorder.StopAsync(fileTime).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not finish the screen video: {e}");
        }
    }

    /// <summary>
    /// Turns <paramref name="recorder"/>'s screen off at the file time of <paramref name="requested"/> (the click),
    /// read off the UI thread (the engine's file time can wait behind a disk flush). Queued behind a start or turn-on
    /// still under way, so it turns that one off once it is done.
    /// </summary>
    private async Task ScreenOffAsync(ScreenRecorder recorder, long requested)
    {
        try
        {
            var time = await Task.Run(() => _engine.FileTime(requested)).ConfigureAwait(false);
            await recorder.ScreenOffAsync(time).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not turn the screen recording off: {e}");
        }
    }

    private static string NoticeText(EndReason reason, bool skipsTranscription) => reason switch
    {
        EndReason.StartFailed when skipsTranscription => Loc.T(
            "The screen could not be recorded. This recording will not be transcribed automatically; you can transcribe it later."),
        EndReason.StartFailed => Loc.T("The screen could not be recorded. The sound is still recording."),
        EndReason.StreamStopped => Loc.T(
            "Screen recording stopped: the screen or window is no longer available. The sound is still recording."),
        EndReason.WriteFailed => Loc.T("Screen recording stopped: could not write the video. The sound is still recording."),
        EndReason.LowDisk => Loc.T("Screen recording stopped: less than 1 GB of disk space left. The sound is still recording."),
        _ => Loc.T("Screen recording could not resume. The sound is still recording."),
    };

    /// <summary>QPC now in 100 ns units, the clock of <see cref="RecordingEngine.FileTime"/>.</summary>
    private static long Now() => Stopwatch.GetElapsedTime(0).Ticks;

    private static void Log(string message) => Debug.WriteLine($"EchoDiar: {message}");

    private const uint MonitorDefaultToNearest = 2; // MONITOR_DEFAULTTONEAREST

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
