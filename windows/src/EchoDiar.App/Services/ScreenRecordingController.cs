using System.Diagnostics;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Screen;

namespace EchoDiar.App.Services;

/// <summary>
/// The screen video of the current recording (macOS ScreenRecordingController.swift): the source picker, the video's
/// state, its <see cref="ScreenRecorder"/> and the outline around a Portion. The view model calls it when the recording
/// starts, pauses, resumes and ends; a video that ends early never stops the audio. The screen can be turned off and on
/// any number of times in a recording: one recorder writes one <see cref="VideoFileName"/>, with the "Screen recording
/// off" frame where the screen was off. UI thread only: the picker's result comes back through an await on it, the
/// recorder's end is posted to it, and the recorder works on the thread pool.
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

    /// <param name="post">Runs an action on the UI thread (the recorder's end arrives on a background thread).</param>
    public ScreenRecordingController(RecordingEngine engine, Action<Action> post)
    {
        _engine = engine;
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
    /// started. Started after the recording began, the file starts with the "Screen recording off" frame. A start that
    /// fails ends the video for this recording with a notice; the sound goes on. No-op when a video already runs or the
    /// engine stopped meanwhile.
    /// </summary>
    public void Start(ScreenSource source, string folder, AppSettings settings, bool skipsTranscription)
    {
        if (_recorder is not null || _engine.CurrentSnapshot().State == RecordingEngine.State.Idle) return;
        var recorder = new ScreenRecorder();
        _recorder = recorder;
        _paused = false;
        _endReason = null;
        _skipsTranscription = skipsTranscription;
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
        _ = StartVideoAsync(recorder, source, generation, Path.Combine(folder, VideoFileName), settings, video);
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
            Start(source, folder, settings, skipsTranscription);
            return;
        }
        _ = TurnOnAsync(recorder, source, Prepare(source));
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
        SyncOutline();
        Changed?.Invoke();
    }

    /// <summary>After the engine resumed.</summary>
    public void Resume()
    {
        if (_recorder is not { } recorder || !_paused) return;
        _paused = false;
        recorder.Resume();
        if (State == ScreenState.Paused) State = ScreenState.Recording;
        SyncOutline();
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
        SyncOutline();
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
        return (_recorder, _finishing);
    }

    // Private

    private async Task StartVideoAsync(
        ScreenRecorder recorder, ScreenSource source, int generation, string path, AppSettings settings, SessionVideo video)
    {
        try
        {
            await recorder.StartAsync(source, settings.ScreenFPS, settings.ScreenQuality, settings.ScreenShowsCursor,
                settings.ScreenShowsClicks, path, RecordingEngine.LowDiskBytes, _engine.FileTime, Loc.T("Screen recording off"));
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
        SyncOutline();
        Changed?.Invoke();
        return _generation;
    }

    /// <summary>The start or turn-on of <paramref name="generation"/> succeeded: the screen records, unless turned off or ended meanwhile.</summary>
    private void Started(int generation)
    {
        if (generation != _generation || State != ScreenState.Starting) return;
        State = _paused ? ScreenState.Paused : ScreenState.Recording;
        SyncOutline();
        Changed?.Invoke();
    }

    /// <summary>The screen is off with the recorder kept: no source, no outline.</summary>
    private void HideScreen()
    {
        State = ScreenState.Off;
        _source = null;
        _generation++;
        SyncOutline();
        Changed?.Invoke();
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
        SyncOutline();
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
}
