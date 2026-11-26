using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Playback;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// Plays the open conversation's audio (macOS <c>PlaybackController</c>): one <see cref="AudioPlayer"/> per file,
/// the play head read at 4 Hz and only while playing, and the transcript segment under it. Lives on the UI thread.
/// </summary>
/// <remarks>
/// The conversation's screen recording, when a <see cref="VideoPaneView"/> is attached (<see cref="AttachVideo"/>),
/// plays on the pane's muted player (video time is audio-file time). With audio, the audio is the clock and the video
/// follows it: played, paused and sought with it, and moved to the audio's play head when it is more than
/// <see cref="DriftTolerance"/> away (checked every 0.5 s while playing). The drift is logged to <see cref="PlaybackLog"/>:
/// 0.5 s after each seek, every 5 s while playing, and at each correction. Without audio, the video's player is the
/// clock: play, pause, seek, the play head and the duration are its own, and nothing is compared or logged.
/// </remarks>
public sealed partial class PlaybackController : ObservableObject, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Above this distance from the audio (seconds), the video is moved to the audio's play head.</summary>
    private const double DriftTolerance = 0.15;

    /// <summary>The drift is checked every this many ticks (0.5 s) while playing.</summary>
    private const int DriftCheckTicks = 2;

    /// <summary>A <c>sample</c> line is logged every this many ticks (5 s) while playing.</summary>
    private const int SampleTicks = 20;

    /// <summary>After a seek or a correction the video needs a moment to land: no correction before this has passed.</summary>
    private const long SettleMilliseconds = 1000;

    /// <summary>How long after a seek its <c>seek</c> line measures the drift.</summary>
    private static readonly TimeSpan SeekLogDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How far past the video's end the play head must be for "No screen recording at this point" (macOS
    /// <c>edgeTolerance</c>): the last frame shows until then. The video starts at file time 0 (its placeholder covers
    /// a screen recording started late), so only the end has a gap.
    /// </summary>
    private const double GapTolerance = 0.5;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _ticker;

    /// <summary>One shot, restarted by each seek, so a scrubber drag logs one <c>seek</c> line.</summary>
    private readonly DispatcherQueueTimer _seekLogTimer;

    private AudioPlayer? _player;
    private string? _path;
    private PlaybackChannels _channels = PlaybackChannels.Mixed;

    /// <summary>Sorted by start.</summary>
    private Transcript.Segment[] _segments = [];

    /// <summary>Bumped whenever the player closes, so a late end of the previous one is ignored.</summary>
    private int _generation;

    private bool _isPlaying;
    private double _currentTime;
    private double _duration;
    private Guid? _activeSegmentId;
    private bool _isReady;
    private string? _error;

    /// <summary>The attached video pane; its player is the video once it is ready.</summary>
    private VideoPaneView? _pane;

    /// <summary>The pane's player this controller set up (<see cref="OnVideoReady"/>); see <see cref="Video"/>.</summary>
    private MediaPlayer? _video;

    private TypedEventHandler<MediaPlayer, object>? _videoEnded;

    /// <summary>Seconds of the video file; 0 without one.</summary>
    private double _videoDuration;

    /// <summary>Ticks since playing started, for the drift checks and the <c>sample</c> lines.</summary>
    private int _ticks;

    /// <summary><see cref="Environment.TickCount64"/> before which the drift is not corrected (a seek or correction landing).</summary>
    private long _settleUntil;

    public PlaybackController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _ticker = dispatcher.CreateTimer();
        _ticker.Interval = TickInterval;
        _ticker.Tick += (_, _) => OnTick();
        _seekLogTimer = dispatcher.CreateTimer();
        _seekLogTimer.Interval = SeekLogDelay;
        _seekLogTimer.IsRepeating = false;
        _seekLogTimer.Tick += (t, _) =>
        {
            t.Stop();
            LogSeek();
        };
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>Seconds; updated at most 4 times a second while playing.</summary>
    public double CurrentTime
    {
        get => _currentTime;
        private set
        {
            if (SetProperty(ref _currentTime, value)) UpdateGap();
        }
    }

    public double Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    /// <summary>The segment under the play head; changes only when the play head enters another segment.</summary>
    public Guid? ActiveSegmentId
    {
        get => _activeSegmentId;
        private set => SetProperty(ref _activeSegmentId, value);
    }

    /// <summary>An audio file is open, or (without one) the attached video is ready to play.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    /// <summary>Why the file could not be opened or played.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>
    /// Opens <paramref name="audioPath"/> (a CAF file) unless it is already open (null closes the player), and sets
    /// the segments to highlight and how the channels play.
    /// </summary>
    public void Load(string? audioPath, IReadOnlyList<Transcript.Segment> segments, PlaybackChannels channels)
    {
        _segments = [.. segments.OrderBy(s => s.Start)];
        if (audioPath != _path)
        {
            CloseAudio();
            _path = audioPath;
            _channels = channels;
            if (audioPath is not null) Open(audioPath, channels);
            // Without audio, the video (once ready) is the clock.
            if (_player is null) UseVideoClock();
        }
        else
        {
            SetChannels(channels);
        }
        if (IsPlaying || CurrentTime > 0) UpdateActiveSegment();
    }

    /// <summary>
    /// Plays both voices in both ears, or as recorded (mic left, system right), switching the open player live: the
    /// play head and whether it plays are kept.
    /// </summary>
    public void SetChannels(PlaybackChannels channels)
    {
        if (channels == _channels) return;
        _channels = channels;
        _player?.SetChannels(channels);
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>Plays from the play head; from the start when it is at the end.</summary>
    public void Play()
    {
        if (_player is null)
        {
            PlayVideoClock();
            return;
        }
        if (Duration <= 0) return;
        try
        {
            _player.Play();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not start playback: {e}");
            Error = Loc.Format("Could not play the audio (%@).", e.Message);
            return;
        }
        Error = null;
        IsPlaying = true;
        UpdateActiveSegment();
        StartTicker();
        // The video starts at the audio's play head (the start when the audio restarted from its end).
        FollowSeek(_player.Position);
    }

    public void Pause()
    {
        Tick();
        _player?.Pause();
        _ticker.Stop();
        IsPlaying = false;
        PauseVideo();
    }

    /// <summary>Seeks to the segment's start and plays (a click on a transcript line).</summary>
    public void SeekTo(Transcript.Segment segment)
    {
        Seek(segment.Start);
        if (!IsPlaying) Play();
    }

    /// <summary>Moves the play head to <paramref name="time"/> seconds, still playing if it was.</summary>
    public void Seek(double time)
    {
        time = Math.Clamp(time, 0, Math.Max(0, Duration));
        if (_player is null)
        {
            // Without audio the video is the clock (when there is one).
            SetVideoPosition(time);
        }
        else
        {
            _player.Seek(time);
            FollowSeek(time);
            _seekLogTimer.Stop();
            _seekLogTimer.Start();
        }
        CurrentTime = time;
        UpdateActiveSegment();
    }

    /// <summary>
    /// Shows the conversation's video on <paramref name="pane"/> (null detaches it): once the pane's video is ready, it
    /// follows the audio, or is the clock without audio, and a click on it plays or pauses. The pane loads and owns its
    /// player; <see cref="Close"/> closes it, a detach leaves it open (paused).
    /// </summary>
    public void AttachVideo(VideoPaneView? pane)
    {
        if (pane == _pane) return;
        if (_pane is { } old)
        {
            old.Ready -= OnVideoReady;
            old.Released -= OnVideoReleased;
            old.PlayPauseRequested -= OnPlayPauseRequested;
            OnVideoReleased();
        }
        _pane = pane;
        if (pane is null) return;
        pane.Ready += OnVideoReady;
        pane.Released += OnVideoReleased;
        pane.PlayPauseRequested += OnPlayPauseRequested;
        if (pane.IsReady) OnVideoReady();
    }

    /// <summary>Stops and releases the audio player and the attached pane's video (the pane stays attached).</summary>
    public void Close()
    {
        CloseAudio();
        _pane?.Close();
        // Already done by the pane's Released when its video was open.
        OnVideoReleased();
    }

    /// <summary>Closes (<see cref="Close"/>) and detaches the video pane.</summary>
    public void Dispose()
    {
        Close();
        AttachVideo(null);
    }

    /// <summary>Stops and releases the audio player; an open video stays, paused at the start.</summary>
    private void CloseAudio()
    {
        _generation++;
        _ticker.Stop();
        _seekLogTimer.Stop();
        _player?.Dispose();
        _player = null;
        _path = null;
        IsReady = false;
        IsPlaying = false;
        CurrentTime = 0;
        Duration = 0;
        ActiveSegmentId = null;
        Error = null;
        PauseVideoAt(0);
    }

    private void Open(string path, PlaybackChannels channels)
    {
        try
        {
            var player = new AudioPlayer(path, channels);
            var generation = _generation;
            player.Ended += error => _dispatcher.TryEnqueue(() => Finished(generation, error));
            _player = player;
            Duration = player.Duration;
            IsReady = true;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open {path} for playback: {e}");
            Error = Loc.Format("Could not open the audio (%@).", e.Message);
        }
    }

    /// <summary>The end of the file (or a device failure): stops, clears the highlight and rewinds.</summary>
    private void Finished(int generation, Exception? error)
    {
        if (generation != _generation) return;
        _ticker.Stop();
        Tick();
        IsPlaying = false;
        if (error is not null)
        {
            // The play head stays where the device failed.
            Debug.WriteLine($"EchoDiar: playback stopped: {error}");
            Error = Loc.Format("Playback stopped (%@).", error.Message);
            PauseVideo();
            return;
        }
        CurrentTime = 0;
        ActiveSegmentId = null;
        PauseVideoAt(0);
    }

    private void StartTicker()
    {
        _ticks = 0;
        _ticker.Start();
    }

    /// <summary>The ticker: reads the play head and, with audio, keeps the video on it. Never throws.</summary>
    private void OnTick()
    {
        try
        {
            Tick();
            if (IsPlaying && _player is not null) FollowAudio();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: playback tick failed: {e}");
        }
    }

    /// <summary>Reads the play head while playing (the video's without audio).</summary>
    private void Tick()
    {
        if (!IsPlaying) return;
        if (_player is null)
        {
            if (VideoTime() is not { } time) return;
            CurrentTime = Math.Min(time, Duration);
            UpdateActiveSegment();
            return;
        }
        CurrentTime = Math.Min(_player.Position, Duration);
        UpdateActiveSegment();
    }

    // MARK: Video

    /// <summary>
    /// The attached pane's player while it is the one set up here, else null (no pane, not ready yet, or closed: the
    /// pane disposes its player at any time).
    /// </summary>
    private MediaPlayer? Video => _video is not null && ReferenceEquals(_pane?.Player, _video) ? _video : null;

    /// <summary>The pane's video is open: picks up its player and duration and puts it on the play head.</summary>
    private void OnVideoReady()
    {
        try
        {
            if (_pane?.Player is not { } video || ReferenceEquals(video, _video)) return;
            DropVideo();
            _video = video;
            _videoEnded = (sender, _) => _dispatcher.TryEnqueue(() => VideoEnded(sender));
            video.MediaEnded += _videoEnded;
            var seconds = video.PlaybackSession.NaturalDuration.TotalSeconds;
            _videoDuration = double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
            if (_player is null)
            {
                UseVideoClock();
                SetVideoPosition(CurrentTime);
            }
            else
            {
                // Late (the audio may already play): the video joins at the audio's play head.
                FollowSeek(IsPlaying ? _player.Position : CurrentTime);
            }
            UpdateGap();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not attach the video: {e}");
        }
    }

    /// <summary>
    /// The pane's video went away (or the pane is detached): it stops following; without audio, the transport stops
    /// with it.
    /// </summary>
    private void OnVideoReleased()
    {
        _seekLogTimer.Stop();
        DropVideo();
        if (_player is not null || !IsReady) return;
        _ticker.Stop();
        IsPlaying = false;
        IsReady = false;
        CurrentTime = 0;
        Duration = 0;
        ActiveSegmentId = null;
    }

    /// <summary>Pauses the video still open, unhooks its end and forgets it.</summary>
    private void DropVideo()
    {
        var video = _video;
        if (video is null) return;
        try
        {
            // Only a player still open: the pane's Close disposed the others (and their handlers with them).
            if (Video is not null)
            {
                video.Pause();
                if (_videoEnded is not null) video.MediaEnded -= _videoEnded;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not detach the video player: {e}");
        }
        _video = null;
        _videoEnded = null;
        _videoDuration = 0;
        if (_pane is not null) _pane.ShowsGap = false;
    }

    private void OnPlayPauseRequested()
    {
        if (IsReady) TogglePlay();
    }

    /// <summary>Without audio: the transport works once the video is ready, on its duration.</summary>
    private void UseVideoClock()
    {
        if (_player is not null || Video is null) return;
        Duration = _videoDuration;
        IsReady = _videoDuration > 0;
    }

    /// <summary>Without audio: plays the video from the play head; from the start when it is at the end.</summary>
    private void PlayVideoClock()
    {
        if (Video is not { } video || !IsReady || Duration <= 0) return;
        if (CurrentTime >= Duration)
        {
            CurrentTime = 0;
            SetVideoPosition(0);
        }
        try
        {
            video.Play();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not play the video: {e}");
            return;
        }
        IsPlaying = true;
        UpdateActiveSegment();
        StartTicker();
    }

    /// <summary>Without audio, the end of the video: stops, clears the highlight and rewinds (as <see cref="Finished"/>).</summary>
    private void VideoEnded(MediaPlayer sender)
    {
        try
        {
            if (!ReferenceEquals(sender, Video) || _player is not null || !IsPlaying) return;
            _ticker.Stop();
            IsPlaying = false;
            CurrentTime = 0;
            ActiveSegmentId = null;
            SetVideoPosition(0);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the end of the video failed: {e}");
        }
    }

    /// <summary>Pauses the video; at the audio's play head with audio, else the play head becomes the video's.</summary>
    private void PauseVideo()
    {
        if (Video is not { } video) return;
        try
        {
            video.Pause();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not pause the video: {e}");
            return;
        }
        if (_player is not null)
        {
            SetVideoPosition(CurrentTime);
        }
        else if (VideoTime() is { } time)
        {
            CurrentTime = Math.Min(time, Duration);
            UpdateActiveSegment();
        }
    }

    /// <summary>Pauses the video at <paramref name="time"/>.</summary>
    private void PauseVideoAt(double time)
    {
        if (Video is not { } video) return;
        try
        {
            video.Pause();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not pause the video: {e}");
        }
        SetVideoPosition(time);
    }

    /// <summary>
    /// With audio: the video goes to <paramref name="time"/> (the audio's play head) and plays when the audio does and
    /// the time is inside the video; past its end it waits on its last frame.
    /// </summary>
    private void FollowSeek(double time)
    {
        if (_player is null || Video is not { } video) return;
        SetVideoPosition(time);
        _settleUntil = Environment.TickCount64 + SettleMilliseconds;
        try
        {
            if (IsPlaying && time < _videoDuration) video.Play();
            else video.Pause();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not follow the audio: {e}");
        }
    }

    /// <summary>
    /// With audio, while playing: every 0.5 s moves the video to the audio's play head when it is more than
    /// <see cref="DriftTolerance"/> away and the play head is inside the video; logs a <c>sample</c> every 5 s.
    /// </summary>
    private void FollowAudio()
    {
        _ticks++;
        if (_ticks % DriftCheckTicks != 0 || _player is not { } player || Video is not { } video) return;
        var audio = player.Position;
        if (audio >= _videoDuration || VideoTime() is not { } time) return;
        if (_ticks % SampleTicks == 0) PlaybackLog.Write(App.DataRoot, "sample", audio, time);
        if (Math.Abs(time - audio) <= DriftTolerance || Environment.TickCount64 < _settleUntil) return;
        PlaybackLog.Write(App.DataRoot, "correct", audio, time);
        SetVideoPosition(audio);
        _settleUntil = Environment.TickCount64 + SettleMilliseconds;
        try
        {
            // Stopped at its end before a seek back into it, for one.
            if (video.PlaybackSession.PlaybackState != MediaPlaybackState.Playing) video.Play();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not restart the video: {e}");
        }
    }

    /// <summary>The <c>seek</c> line, 0.5 s after the last seek: the drift once both have landed.</summary>
    private void LogSeek()
    {
        try
        {
            if (_player is not { } player || Video is null) return;
            var audio = player.Position;
            if (audio >= _videoDuration || VideoTime() is not { } time) return;
            PlaybackLog.Write(App.DataRoot, "seek", audio, time);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not log the seek: {e}");
        }
    }

    /// <summary>The video's position in seconds; null without a video or when it cannot be read.</summary>
    private double? VideoTime()
    {
        if (Video is not { } video) return null;
        try
        {
            var seconds = video.PlaybackSession.Position.TotalSeconds;
            return double.IsFinite(seconds) ? Math.Max(0, seconds) : null;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the video position: {e}");
            return null;
        }
    }

    /// <summary>Moves the video to <paramref name="time"/> (kept inside it); nothing without a video.</summary>
    private void SetVideoPosition(double time)
    {
        if (Video is not { } video) return;
        try
        {
            video.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(time, 0, _videoDuration));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not move the video: {e}");
        }
    }

    /// <summary>"No screen recording at this point" while the play head is past the video's end.</summary>
    private void UpdateGap()
    {
        if (_pane is null) return;
        _pane.ShowsGap = Video is not null && CurrentTime > _videoDuration + GapTolerance;
    }

    /// <summary>The last segment starting at or before the play head, while the play head is inside it.</summary>
    private void UpdateActiveSegment()
    {
        var index = Transcript.SegmentIndexAt(_segments, CurrentTime);
        ActiveSegmentId = index >= 0 ? _segments[index].Id : null;
    }
}
