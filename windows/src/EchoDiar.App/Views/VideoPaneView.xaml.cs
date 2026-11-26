using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.Platform.Screen;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The conversation's screen recording (macOS VideoPaneView.swift): the picture of a muted <see cref="MediaPlayer"/>
/// (the sound plays from the conversation's audio), aspect-fit, without transport controls. It does not play by
/// itself: a click raises <see cref="PlayPauseRequested"/> and the playback controller drives <see cref="Player"/>
/// (position, play, pause, rate). A placeholder of the same card shows at once, its ring only after 150 ms; the
/// picture once the video is open (<see cref="Ready"/>). "No screen recording at this point" covers it while
/// <see cref="ShowsGap"/>, and an HEVC video shows a note instead when no HEVC decoder is installed. The file is found,
/// checked and opened off the UI thread; <see cref="Close"/> releases it (before the file is deleted).
/// </summary>
public sealed partial class VideoPaneView : UserControl
{
    /// <summary>How long the placeholder shows without its ring: a ring shown for a frame or two reads as a flicker.</summary>
    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);

    private static readonly UISettings UISettings = new();

    /// <summary>An HEVC decoder was found once: kept for the process. Not found is looked up again (it may be installed since).</summary>
    private static volatile bool _hevcDecoderSeen;

    private readonly DispatcherQueue _dispatcher;

    /// <summary>Fades the placeholder's ring in after <see cref="SpinnerDelay"/>.</summary>
    private readonly DispatcherQueueTimer _spinnerTimer;

    private MediaPlayer? _player;
    private MediaSource? _source;

    /// <summary>The video loaded; null when none.</summary>
    private string? _path;

    /// <summary>Bumped by <see cref="Close"/>, so an open still running or a late player event is dropped.</summary>
    private int _generation;

    /// <summary>The loaded video is HEVC.</summary>
    private bool _isHevc;

    /// <summary>The loaded video is HEVC and cannot be decoded here: the note shows instead of the picture.</summary>
    private bool _hevcMissing;

    /// <summary>The video could not be opened (other than HEVC): the card stays empty.</summary>
    private bool _failed;

    private bool _showsGap;

    public VideoPaneView()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        _spinnerTimer = DispatcherQueue.CreateTimer();
        _spinnerTimer.Interval = SpinnerDelay;
        _spinnerTimer.IsRepeating = false;
        _spinnerTimer.Tick += (t, _) =>
        {
            t.Stop();
            LoadingRing.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition() : null;
            LoadingRing.IsActive = true;
            LoadingRing.Opacity = 1;
        };
        ClickLayer.Tapped += (_, e) =>
        {
            e.Handled = true;
            PlayPauseRequested?.Invoke();
        };
    }

    /// <summary>The picture was clicked: the playback controller plays or pauses (the pane never does by itself).</summary>
    public event Action? PlayPauseRequested;

    /// <summary>The video is open: <see cref="Player"/> is set and can be driven. Raised on the UI thread.</summary>
    public event Action? Ready;

    /// <summary>
    /// The open video went away (<see cref="Close"/>, another <see cref="Load"/>, or it failed after
    /// <see cref="Ready"/>): <see cref="Player"/> is null from now on. Raised on the UI thread, only after a
    /// <see cref="Ready"/>.
    /// </summary>
    public event Action? Released;

    /// <summary>The video is open (<see cref="Ready"/> was raised for it).</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// The open video's muted player, for the playback controller (position, play, pause, rate); null until
    /// <see cref="Ready"/> and after <see cref="Close"/>. Owned by the pane: do not dispose it.
    /// </summary>
    public MediaPlayer? Player => IsReady ? _player : null;

    /// <summary>The play head is outside the recorded picture: "No screen recording at this point" covers it.</summary>
    public bool ShowsGap
    {
        get => _showsGap;
        set
        {
            if (value == _showsGap) return;
            _showsGap = value;
            UpdateCover();
        }
    }

    /// <summary>
    /// Shows <paramref name="videoPath"/> (an mp4) unless it is already loaded: the placeholder at once, the picture
    /// once it is open. Null closes the video (<see cref="Close"/>).
    /// </summary>
    public void Load(string? videoPath)
    {
        if (videoPath == _path) return;
        Close();
        if (videoPath is null) return;
        _path = videoPath;
        _spinnerTimer.Start();
        UpdateCover();
        StartupTrace.Write($"video pane: placeholder for {videoPath}");
        _ = OpenAsync(videoPath, _generation);
    }

    /// <summary>
    /// Stops and releases the video at once (the file can be deleted after it) and shows the empty card. Safe to call
    /// at any time, again too.
    /// </summary>
    public void Close()
    {
        _generation++;
        _spinnerTimer.Stop();
        var wasReady = IsReady;
        var player = _player;
        var source = _source;
        _player = null;
        _source = null;
        _path = null;
        IsReady = false;
        _isHevc = false;
        _hevcMissing = false;
        _failed = false;
        _showsGap = false;
        if (player is not null)
        {
            Picture.SetMediaPlayer(null);
            Release(player, source);
        }
        UpdateCover();
        if (wasReady) Released?.Invoke();
    }

    /// <summary>
    /// Finds the file, reads its codec and makes the muted player off the UI thread, then shows it here; an HEVC video
    /// without a decoder gets the note instead and is not opened.
    /// </summary>
    private async Task OpenAsync(string path, int generation)
    {
        Opened opened;
        try
        {
            opened = await Task.Run(() => CreatePlayerAsync(path, generation));
        }
        catch (Exception e)
        {
            if (generation != _generation) return;
            Debug.WriteLine($"EchoDiar: could not open the video {path}: {e}");
            _failed = true;
            _spinnerTimer.Stop();
            UpdateCover();
            return;
        }
        if (generation != _generation)
        {
            // Closed or another video loaded meanwhile.
            if (opened.Player is not null) Release(opened.Player, opened.Source);
            return;
        }
        _isHevc = opened.IsHevc;
        if (opened.Player is null)
        {
            _hevcMissing = true;
            _spinnerTimer.Stop();
            UpdateCover();
            return;
        }
        _player = opened.Player;
        _source = opened.Source;
        Picture.SetMediaPlayer(_player);
        // Opens asynchronously: MediaOpened or MediaFailed follows.
        _player.Source = _source;
    }

    private readonly record struct Opened(MediaPlayer? Player, MediaSource? Source, bool IsHevc);

    /// <summary>Off the UI thread: no player when the video is HEVC and no HEVC decoder exists.</summary>
    private async Task<Opened> CreatePlayerAsync(string path, int generation)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var hevc = await IsHevcAsync(file);
        if (hevc && !HevcDecoderFound()) return new Opened(null, null, true);
        var source = MediaSource.CreateFromStorageFile(file);
        var player = new MediaPlayer { AutoPlay = false, IsMuted = true };
        // Media keys and the system's media controls must not drive it: the playback controller does.
        player.CommandManager.IsEnabled = false;
        player.MediaOpened += (_, _) => _dispatcher.TryEnqueue(() => OnOpened(generation));
        player.MediaFailed += (_, args) =>
        {
            var reason = $"{args.Error} {args.ExtendedErrorCode?.HResult:X8} {args.ErrorMessage}";
            _dispatcher.TryEnqueue(() => OnFailed(generation, reason));
        };
        return new Opened(player, source, hevc);
    }

    /// <summary>The video's codec is HEVC; false when it cannot be read (the video is then opened as usual).</summary>
    private static async Task<bool> IsHevcAsync(StorageFile file)
    {
        try
        {
            var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
            return string.Equals(profile.Video?.Subtype, MediaEncodingSubtypes.Hevc, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the codec of {file.Path}: {e}");
            return false;
        }
    }

    /// <summary>
    /// An HEVC decoder exists (Media Foundation's decoder MFTs, the Phase 1 gate's <c>hevcDecoderFound</c>); off the
    /// UI thread. True when the lookup fails: the video is then opened, and a failure still shows the note.
    /// </summary>
    private static bool HevcDecoderFound()
    {
        if (_hevcDecoderSeen) return true;
        try
        {
            return _hevcDecoderSeen = VideoEncoder.Detect().HevcDecoderFound;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not look for an HEVC decoder: {e}");
            return true;
        }
    }

    private void OnOpened(int generation)
    {
        if (generation != _generation || _player is null) return;
        _spinnerTimer.Stop();
        IsReady = true;
        UpdateCover();
        StartupTrace.Write($"video pane: opened {_path}");
        Ready?.Invoke();
    }

    /// <summary>The video could not be opened or played: the HEVC note for an HEVC video, else the empty card.</summary>
    private void OnFailed(int generation, string reason)
    {
        if (generation != _generation) return;
        Debug.WriteLine($"EchoDiar: the video {_path} failed: {reason}");
        _spinnerTimer.Stop();
        var wasReady = IsReady;
        IsReady = false;
        if (_isHevc) _hevcMissing = true;
        else _failed = true;
        UpdateCover();
        if (wasReady) Released?.Invoke();
    }

    /// <summary>
    /// What covers the picture, first match: the HEVC note; the placeholder until the video is open (its ring once
    /// <see cref="_spinnerTimer"/> fired); the gap note; else the picture.
    /// </summary>
    private void UpdateCover()
    {
        string? note = null;
        if (_hevcMissing) note = Loc.T("This video uses HEVC. Install HEVC Video Extensions from the Microsoft Store to play it.");
        else if (IsReady && _showsGap) note = Loc.T("No screen recording at this point");
        Picture.Opacity = IsReady && note is null ? 1 : 0;
        NoteText.Text = note ?? "";
        NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        var loading = _path is not null && !IsReady && !_hevcMissing && !_failed;
        if (!loading)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Opacity = 0;
        }
    }

    /// <summary>Stops the player and closes its file at once; the disposes run even when detaching the source fails.</summary>
    private static void Release(MediaPlayer player, MediaSource? source)
    {
        try
        {
            player.Source = null;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not detach the video: {e}");
        }
        finally
        {
            source?.Dispose();
            player.Dispose();
        }
    }
}
