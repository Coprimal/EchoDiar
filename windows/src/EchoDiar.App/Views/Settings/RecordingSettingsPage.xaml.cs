using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Recording (macOS RecordingSettingsView.swift), the Microphone, Meetings, Auto-stop and After recording
/// inner tabs, and Screen with <see cref="Features.ScreenRecording"/>; the title bar picks the tab
/// (<see cref="AppViewModel.SubTabOf"/>). Every setting shows <see cref="SettingsViewModel"/> and changes it; the change
/// is saved after its delay. The microphones are listed again each time the page or its Microphone tab shows (macOS
/// follows device changes live).
/// </summary>
public sealed partial class RecordingSettingsPage : UserControl
{
    public const string MicrophoneTab = "microphone";
    public const string MeetingsTab = "meetings";
    public const string AutoStopTab = "autoStop";
    public const string AfterRecordingTab = "afterRecording";
    public const string ScreenTab = "screen";

    /// <summary>macOS <c>SettingsTab.recording.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
    [
        new(MicrophoneTab, "Microphone"),
        new(MeetingsTab, "Meetings"),
        new(AutoStopTab, "Auto-stop"),
        new(AfterRecordingTab, "After recording"),
        .. Features.ScreenRecording ? [new SettingsSubTab(ScreenTab, "Screen")] : Array.Empty<SettingsSubTab>(),
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, Recording), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Microphone", ["input device", "mic", "audio input"], MicrophoneTab),
        new("When the microphone disconnects", ["unplug", "headphones", "route change"], MicrophoneTab),
        new("Tray icon while recording",
            ["tray", "notification area", "status item", "elapsed time", "timer", "file size", "icon"], MicrophoneTab),
        new("Detect meetings", ["auto record", "meeting apps", .. MeetingApps.Known.Select(a => a.Name)], MeetingsTab),
        new("Attach the indicator to the meeting window", ["floating indicator", "pill", "anchor"], MeetingsTab),
        new("Stop when the app is quiet for", ["auto-stop", "inactive", "idle"], AutoStopTab),
        new("Stop when the call seems over for", ["auto-stop", "call ended", "hang up"], AutoStopTab),
        new("Stop after silence of", ["auto-stop", "quiet", "silent"], AutoStopTab),
        new("Stop after a recording length of", ["auto-stop", "maximum duration", "time limit"], AutoStopTab),
        new("Notify when a meeting seems to end", ["auto-stop", "notification", "alert", "banner", "meeting end"], AutoStopTab),
        new("Transcribe automatically", ["auto transcribe", "after stop"], AfterRecordingTab),
        new("Tell speakers apart", ["diarization", "speakers", "who spoke"], AfterRecordingTab),
        new("Number of other speakers", ["speaker count", "diarization", "people"], AfterRecordingTab),
        new(DeferTitle, ["battery", "battery saver", "energy saver", "power", "defer"], AfterRecordingTab),
        .. Features.ScreenRecording
            ?
            [
                new SettingsEntry("Frame rate", ["fps", "frames per second", "60 fps", "video", "screen recording"], ScreenTab),
                new SettingsEntry("Quality",
                    ["video quality", "file size", "MB", "smaller", "balanced", "sharper", "screen recording"], ScreenTab),
                new SettingsEntry("Transcribe screen recordings", ["screen only", "video", "no transcript"], ScreenTab),
                new SettingsEntry("Show the cursor", ["pointer", "mouse", "video", "screen recording"], ScreenTab),
                new SettingsEntry("Highlight clicks", ["mouse clicks", "pointer", "video", "screen recording"], ScreenTab),
                new SettingsEntry("Camera bubble", ["webcam", "camera", "face", "overlay", "video"], ScreenTab),
                new SettingsEntry("Blur background", ["blur", "background", "portrait", "camera"], ScreenTab),
            ]
            : Array.Empty<SettingsEntry>(),
    ];

    /// <summary>
    /// macOS "Wait when the Mac is hot or in Low Power Mode": Windows has no thermal state, only energy saver (Windows-only
    /// keys).
    /// </summary>
    private const string DeferTitle = "Wait while energy saver is on";

    private const string DeferDescription = "Transcription starts once energy saver is off.";

    /// <summary>macOS's text without its Accessibility sentence: Windows needs no permission to follow a window.</summary>
    private const string AttachDescription = "Keeps it at the window's top-right corner.";

    /// <summary>The route combo's items, in order (macOS: Ask to stop, Keep recording, Stop recording).</summary>
    private static readonly RouteChangeBehavior[] RouteBehaviors = [RouteChangeBehavior.Ask, RouteChangeBehavior.Continue, RouteChangeBehavior.Stop];

    /// <summary>The tray combo's items, in order (macOS: Icon only, Elapsed time, File size).</summary>
    private static readonly MenuBarRecordingInfo[] TrayInfos =
        [MenuBarRecordingInfo.IconOnly, MenuBarRecordingInfo.ElapsedTime, MenuBarRecordingInfo.FileSize];

    /// <summary>The quality combo's presets, in order (macOS <c>ScreenQuality.presets</c>: Smaller, Balanced, Sharper).</summary>
    private static readonly double[] QualityPresets = [0.60, 0.75, 0.85];

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly ModelsViewModel _models;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>The microphones last listed.</summary>
    private IReadOnlyList<AudioInputDevice> _microphones = [];

    /// <summary>The microphone combo's endpoint ids, in item order (null = System default).</summary>
    private List<string?> _micIds = [];

    /// <summary>A listing is under way.</summary>
    private bool _listing;

    /// <summary>The 60 fps confirmation is up.</summary>
    private bool _confirming60FPS;

    /// <summary>Shows the 60 fps confirmation in the toolbar's dialog queue; false when another dialog is open.</summary>
    internal Func<ContentDialog, Task<bool>>? ShowDialog { get; set; }

    /// <summary>One row per meeting app: its app, its combo and the behaviors of the combo's items.</summary>
    private readonly List<(MeetingApp App, ComboBox Combo, MeetingBehavior[] Behaviors)> _appRows = [];

    public RecordingSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        _models = model.ModelsModel;
        InitializeComponent();
        AttachCard.Description = Loc.T(AttachDescription);
        DeferCard.Header = Loc.T(DeferTitle);
        DeferCard.Description = Loc.T(DeferDescription);
        DeferCard.Tag = DeferTitle;
        AutomationProperties.SetName(DeferToggle, Loc.T(DeferTitle));
        SpeakerCountCombo.Items.Add(Loc.T("Auto"));
        var (min, max) = AppSettings.SpeakerCountHintRange;
        for (var count = min; count <= max; count++) SpeakerCountCombo.Items.Add(count.ToString(CultureInfo.CurrentCulture));
        foreach (var fps in AppSettings.ScreenFPSOptions) FrameRateCombo.Items.Add(Loc.Format("%lld fps", fps));
        AddAppRows();
        SetUpNumber(SourceInactiveBox, SourceInactiveCard, MeetingEndMonitorConfiguration.SourceInactiveRange, "s",
            (s, v) => s with { EndMonitor = s.EndMonitor with { SourceInactiveSeconds = v } });
        SetUpNumber(CallEndedBox, CallEndedCard, MeetingEndMonitorConfiguration.CallEndedRange, "s",
            (s, v) => s with { EndMonitor = s.EndMonitor with { CallEndedSeconds = v } });
        SetUpNumber(SilenceBox, SilenceCard, MeetingEndMonitorConfiguration.SilenceRange, "min",
            (s, v) => s with { EndMonitor = s.EndMonitor with { SilenceMinutes = v } });
        SetUpNumber(MaxDurationBox, MaxDurationCard, MeetingEndMonitorConfiguration.MaxDurationRange, "min",
            (s, v) => s with { EndMonitor = s.EndMonitor with { MaxDurationMinutes = v } });

        MicCombo.SelectionChanged += (_, _) =>
        {
            var index = MicCombo.SelectedIndex;
            if (_syncing || index < 0 || index >= _micIds.Count) return;
            var id = _micIds[index];
            _settings.Update(s => s with { MicDeviceUID = id });
        };
        RouteCombo.SelectionChanged += (_, _) =>
        {
            var index = RouteCombo.SelectedIndex;
            if (!_syncing && index >= 0) _settings.Update(s => s with { RouteChangeBehavior = RouteBehaviors[index] });
        };
        TrayInfoCombo.SelectionChanged += (_, _) =>
        {
            var index = TrayInfoCombo.SelectedIndex;
            if (!_syncing && index >= 0) _settings.MenuBarRecordingInfo = TrayInfos[index];
        };
        AttachToggle.Toggled += (_, _) => Set(() => _settings.Update(s => s with { AttachPillToWindow = AttachToggle.IsOn }));
        DetectToggle.Toggled += (_, _) => Set(() => _settings.Update(s => s with { MeetingDetection = DetectToggle.IsOn }));
        EndPromptNotificationToggle.Toggled += (_, _) =>
            Set(() => _settings.Update(s => s with { EndPromptNotification = EndPromptNotificationToggle.IsOn }));
        NotificationSettingsButton.Click += (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));
        AutoTranscribeToggle.Toggled += (_, _) => Set(() => _settings.AutoTranscribe = AutoTranscribeToggle.IsOn);
        DiarizationToggle.Toggled += (_, _) => Set(() => _settings.Diarization = DiarizationToggle.IsOn);
        SpeakerCountCombo.SelectionChanged += (_, _) =>
        {
            // Item 0 is Auto (no hint), item n is n speakers from the range's start.
            var index = SpeakerCountCombo.SelectedIndex;
            if (!_syncing && index >= 0) _settings.SpeakerCountHint = index == 0 ? null : min + index - 1;
        };
        DeferToggle.Toggled += (_, _) => Set(() => _settings.DeferTranscriptionWhenConstrained = DeferToggle.IsOn);
        FrameRateCombo.SelectionChanged += (_, _) =>
        {
            var index = FrameRateCombo.SelectedIndex;
            if (!_syncing && index >= 0) _ = SetFrameRateAsync(AppSettings.ScreenFPSOptions[index]);
        };
        QualityCombo.SelectionChanged += (_, _) =>
        {
            var index = QualityCombo.SelectedIndex;
            if (!_syncing && index >= 0) _settings.ScreenQuality = QualityPresets[index];
        };
        ScreenTranscribesToggle.Toggled += (_, _) => Set(() => _settings.ScreenTranscribes = ScreenTranscribesToggle.IsOn);
        ShowCursorToggle.Toggled += (_, _) => Set(() => _settings.ScreenShowsCursor = ShowCursorToggle.IsOn);
        ShowClicksToggle.Toggled += (_, _) => Set(() => _settings.ScreenShowsClicks = ShowClicksToggle.IsOn);
        CameraToggle.Toggled += (_, _) => Set(() => _settings.ScreenCamera = CameraToggle.IsOn);
        CameraBlurToggle.Toggled += (_, _) => Set(() => _settings.ScreenCameraBlur = CameraBlurToggle.IsOn);

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            _models.PropertyChanged += OnModelsChanged;
            Refresh();
            ShowTab();
            _ = ListMicrophonesAsync();
            // macOS refreshInstalledModels: the number of speakers' note follows the speaker model.
            _ = _model.RefreshModelsAsync();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
            _models.PropertyChanged -= OnModelsChanged;
        };
    }

    private string? Tab => _model.SubTabOf(SettingsSearch.Recording);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model.IsClosing) return;
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.SettingsSubTab):
                ShowTab();
                if (Tab == MicrophoneTab) _ = ListMicrophonesAsync();
                // Turned on or off in Windows Settings since the page showed.
                if (Tab == AutoStopTab) RefreshNotificationsOff();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_model.IsClosing) Refresh();
    }

    private void OnModelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelsViewModel.DiarizerAvailable) && !_model.IsClosing) Refresh();
    }

    private void Set(Action change)
    {
        if (!_syncing) change();
    }

    /// <summary>The picked tab's panel.</summary>
    private void ShowTab()
    {
        var tab = Tab;
        MicrophonePanel.Visibility = tab == MicrophoneTab ? Visibility.Visible : Visibility.Collapsed;
        MeetingsPanel.Visibility = tab == MeetingsTab ? Visibility.Visible : Visibility.Collapsed;
        AutoStopPanel.Visibility = tab == AutoStopTab ? Visibility.Visible : Visibility.Collapsed;
        AfterRecordingPanel.Visibility = tab == AfterRecordingTab ? Visibility.Visible : Visibility.Collapsed;
        ScreenPanel.Visibility = tab == ScreenTab && Features.ScreenRecording ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Every setting control shows the current settings.</summary>
    private void Refresh()
    {
        var settings = _settings.Settings;
        _syncing = true;
        try
        {
            RefreshMicrophones();
            var route = settings.RouteChangeBehavior;
            RouteCombo.SelectedIndex = Array.IndexOf(RouteBehaviors, route);
            RouteCard.Description = Loc.T(route switch
            {
                RouteChangeBehavior.Continue => "Recording switches to the default input.",
                RouteChangeBehavior.Stop => "Recording stops at once.",
                _ => "Recording continues with the default input until then.",
            });
            TrayInfoCombo.SelectedIndex = Array.IndexOf(TrayInfos, settings.MenuBarRecordingInfo);

            AttachToggle.IsOn = settings.AttachPillToWindow;
            DetectToggle.IsOn = settings.MeetingDetection;
            foreach (var (app, combo, behaviors) in _appRows)
            {
                // "Ask" is stored as no entry, the default; a stored "Always" for a browser shows as Ask (it asks).
                var behavior = settings.PerAppBehavior.GetValueOrDefault(app.AppExe, MeetingBehavior.Ask);
                var index = Array.IndexOf(behaviors, behavior);
                combo.SelectedIndex = index >= 0 ? index : 0;
                combo.IsEnabled = settings.MeetingDetection;
            }

            var endMonitor = settings.EndMonitor;
            SourceInactiveBox.Value = Clamped(endMonitor.SourceInactiveSeconds, MeetingEndMonitorConfiguration.SourceInactiveRange);
            CallEndedBox.Value = Clamped(endMonitor.CallEndedSeconds, MeetingEndMonitorConfiguration.CallEndedRange);
            SilenceBox.Value = Clamped(endMonitor.SilenceMinutes, MeetingEndMonitorConfiguration.SilenceRange);
            MaxDurationBox.Value = Clamped(endMonitor.MaxDurationMinutes, MeetingEndMonitorConfiguration.MaxDurationRange);
            EndPromptNotificationToggle.IsOn = settings.EndPromptNotification;
            RefreshNotificationsOff();

            AutoTranscribeToggle.IsOn = settings.AutoTranscribe;
            DiarizationToggle.IsOn = settings.Diarization;
            // macOS shows the number only while speakers are told apart.
            SpeakerCountCard.Visibility = settings.Diarization ? Visibility.Visible : Visibility.Collapsed;
            var (min, _) = AppSettings.SpeakerCountHintRange;
            SpeakerCountCombo.SelectedIndex = settings.SpeakerCountHint is { } hint ? hint - min + 1 : 0;
            SpeakerCountCard.Description = Loc.T(_models.DiarizerAvailable
                ? "A hint for telling the system audio's speakers apart."
                : "Needs the speaker model: download it in Settings › AI Providers › Local AI.");
            DeferToggle.IsOn = settings.DeferTranscriptionWhenConstrained;

            FrameRateCombo.SelectedIndex = AppSettings.ScreenFPSOptions.ToList().IndexOf(settings.ScreenFPS);
            QualityCombo.SelectedIndex = NearestQualityPreset(settings.ScreenQuality);
            ScreenTranscribesToggle.IsOn = settings.ScreenTranscribes;
            ShowCursorToggle.IsOn = settings.ScreenShowsCursor;
            ShowClicksToggle.IsOn = settings.ScreenShowsClicks;
            CameraToggle.IsOn = settings.ScreenCamera;
            CameraBlurToggle.IsOn = settings.ScreenCameraBlur;
            CameraBlurCard.IsEnabled = settings.ScreenCamera;
        }
        finally
        {
            _syncing = false;
        }
    }

    // Microphone

    /// <summary>Lists the active inputs off the UI thread, then shows them.</summary>
    private async Task ListMicrophonesAsync()
    {
        if (_listing) return;
        _listing = true;
        try
        {
            var microphones = await Task.Run(AudioRouteMonitor.ListInputDevices);
            if (_model.IsClosing) return;
            _microphones = [.. microphones.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not list the microphones: {e}");
            return;
        }
        finally
        {
            _listing = false;
        }
        _syncing = true;
        try
        {
            RefreshMicrophones();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>System default, every listed input, and the saved one when it is not listed (macOS "Unavailable device").</summary>
    private void RefreshMicrophones()
    {
        var saved = _settings.Settings.MicDeviceUID;
        List<string?> ids = [null, .. _microphones.Select(m => m.Id)];
        List<string> names = [Loc.T("System default"), .. _microphones.Select(m => m.Name)];
        if (saved is not null && !ids.Contains(saved))
        {
            ids.Add(saved);
            names.Add(Loc.T("Unavailable device"));
        }
        if (!ids.SequenceEqual(_micIds))
        {
            _micIds = ids;
            MicCombo.Items.Clear();
            foreach (var name in names) MicCombo.Items.Add(name);
        }
        MicCombo.SelectedIndex = _micIds.IndexOf(saved);
    }

    // Meetings

    /// <summary>A row per detectable app: Ask, Always record (not for browsers), Ignore.</summary>
    private void AddAppRows()
    {
        foreach (var app in _model.DetectableMeetingApps)
        {
            MeetingBehavior[] behaviors = app.IsBrowser
                ? [MeetingBehavior.Ask, MeetingBehavior.Ignore]
                : [MeetingBehavior.Ask, MeetingBehavior.Always, MeetingBehavior.Ignore];
            var combo = new ComboBox { MinWidth = 200 };
            AutomationProperties.SetName(combo, app.Name);
            foreach (var behavior in behaviors)
            {
                combo.Items.Add(Loc.T(behavior switch
                {
                    MeetingBehavior.Always => "Always record",
                    MeetingBehavior.Ignore => "Ignore",
                    _ => "Ask",
                }));
            }
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (!_syncing && index >= 0) SetBehavior(app, behaviors[index]);
            };
            AppsList.Children.Add(new SettingsCard { Header = app.Name, Content = combo });
            _appRows.Add((app, combo, behaviors));
        }
    }

    /// <summary>"Ask" is stored as no entry, the default.</summary>
    private void SetBehavior(MeetingApp app, MeetingBehavior behavior) => _settings.Update(s =>
    {
        var behaviors = new Dictionary<string, MeetingBehavior>(s.PerAppBehavior);
        if (behavior == MeetingBehavior.Ask) behaviors.Remove(app.AppExe);
        else behaviors[app.AppExe] = behavior;
        return s with { PerAppBehavior = behaviors };
    });

    // Auto-stop

    /// <summary>
    /// A whole number typed or stepped within <paramref name="range"/> (macOS NumberRow): a value outside it is
    /// clamped, a cleared box shows the saved value again.
    /// </summary>
    private void SetUpNumber(
        NumberBox box, SettingsCard card, (int Min, int Max) range, string unit, Func<AppSettings, int, AppSettings> change)
    {
        box.Minimum = range.Min;
        box.Maximum = range.Max;
        ToolTipService.SetToolTip(card, $"{range.Min}–{range.Max} {Loc.T(unit)}");
        box.ValueChanged += (_, args) =>
        {
            if (_syncing) return;
            if (double.IsNaN(args.NewValue))
            {
                Refresh();
                return;
            }
            var value = Math.Clamp((int)Math.Round(args.NewValue), range.Min, range.Max);
            _settings.Update(s => change(s, value));
            // A typed value outside the range shows clamped.
            if (value != args.NewValue) Refresh();
        };
    }

    private static int Clamped(int value, (int Min, int Max) range) => Math.Clamp(value, range.Min, range.Max);

    // Screen

    /// <summary>
    /// Picking 60 fps from another rate asks first (much larger files), and the stored rate stays until "Use 60 fps";
    /// Cancel, or another dialog open, shows the stored rate again. The other rates apply at once (macOS <c>frameRate</c>).
    /// </summary>
    private async Task SetFrameRateAsync(int fps)
    {
        if (fps != 60 || _settings.ScreenFPS == 60)
        {
            _settings.ScreenFPS = fps;
            return;
        }
        if (_confirming60FPS || XamlRoot is not { } root || ShowDialog is not { } show)
        {
            Refresh();
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Title = Loc.T("Record at 60 frames per second?"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T("Files can be several times larger.") },
            PrimaryButtonText = Loc.T("Use 60 fps"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        // The queue throws the result away: "Use 60 fps" is read from its click.
        var confirmed = false;
        dialog.PrimaryButtonClick += (_, _) => confirmed = true;
        _confirming60FPS = true;
        try
        {
            await show(dialog);
        }
        finally
        {
            _confirming60FPS = false;
        }
        if (_model.IsClosing) return;
        if (confirmed) _settings.ScreenFPS = 60;
        else Refresh();
    }

    /// <summary>The index of the preset nearest <paramref name="quality"/>, the first on a tie (macOS <c>ScreenQuality.nearest</c>).</summary>
    private static int NearestQualityPreset(double quality)
    {
        var nearest = 0;
        for (var i = 1; i < QualityPresets.Length; i++)
        {
            if (Math.Abs(QualityPresets[i] - quality) < Math.Abs(QualityPresets[nearest] - quality)) nearest = i;
        }
        return nearest;
    }

    /// <summary>The note and its Windows Settings button while notifications are off for EchoDiar (macOS: denied).</summary>
    private void RefreshNotificationsOff() =>
        NotificationsOffCard.Visibility = EndPromptNotifier.NotificationsOff() ? Visibility.Visible : Visibility.Collapsed;
}
