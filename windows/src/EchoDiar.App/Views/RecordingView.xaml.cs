using System.ComponentModel;
using System.Numerics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The recording page (macOS RecordingViews.swift RecordingDetailView, RecordingHeader, ChannelMeters and
/// RecordingWave), opened from the recording's card: the title, "Catch up", the calendar suggestion and the capture
/// warnings, pinned on top, then the live transcript (or the wave and why there is none) on the left and a panel on the
/// right with its Catch-up | Chat tabs (<see cref="AppViewModel.RecordingTab"/>), the catch-up model and the answer
/// language over the catch-ups (macOS CatchUpPanel) or the live chat. The meters and the wave move at 15 Hz while the
/// window is shown and the recording is not paused; the wave stands still when Windows animations are off (Reduce
/// Motion).
/// </summary>
public sealed partial class RecordingView : UserControl
{
    private const int BarCount = 24;
    private const double WaveHeight = 64;

    /// <summary>The meters side by side: each bar's width, and the gap between mic and system.</summary>
    private const double RowBarWidth = 72;
    private const double RowMetersSpacing = 12;

    /// <summary>The meters mic over system (the XAML's layout): their width, the gap, the labels' minimum width.</summary>
    private const double StackedMetersWidth = 120;
    private const double StackedMetersSpacing = 3;
    private const double StackedLabelMinWidth = 44;

    private static readonly UISettings UISettings = new();

    private readonly AppViewModel _model;
    private readonly FrameTicker _ticker;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _history = new double[BarCount];
    private int _micPercent = -1;
    private int _systemPercent = -1;
    private RecordingEngine.State? _shownState;

    /// <summary>The meters' layout as last applied: side by side, mic over system, or none yet (the XAML's).</summary>
    private bool? _metersInRow;

    /// <summary>The catch-up cards by id, kept while shown, so a saved catch-up's Markdown is not rendered again.</summary>
    private readonly Dictionary<Guid, CatchUpCard> _catchUpCards = [];

    /// <summary>The open state the user gave cards with a click; the others: the newest open, the older ones collapsed.</summary>
    private readonly Dictionary<Guid, bool> _catchUpOpenStates = [];

    /// <summary>The clicks during a search, which win over the open state it gives; cleared when the query changes.</summary>
    private readonly Dictionary<Guid, bool> _catchUpSearchOpenStates = [];

    /// <summary>The card of <see cref="AppViewModel.CatchUpRun"/>: the only one <see cref="AppViewModel.CatchUpTextChanged"/> updates.</summary>
    private CatchUpCard? _runCard;

    /// <summary>
    /// What the catch-ups were last scrolled to the top for: their count, the run's id and whether it failed; another
    /// one scrolls again.
    /// </summary>
    private (int Count, Guid? Run, bool Failed)? _scrolledCatchUps;

    /// <summary>"Catch up"'s tooltip as last set: one set again while shown closes under the pointer (the captions change it often).</summary>
    private string? _catchUpTip;

    public RecordingView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        _ticker = new FrameTicker(model, this, OnTick);
        for (var i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle { Width = 4, Height = 4, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center };
            Wave.Children.Add(_bars[i]);
        }
        LanguageHost.Content = new LanguagePicker(model, shortLabel: false);
        LiveHost.Child = new LiveTranscriptView(model);
        GlobalTapWarning.Message = Loc.T("This app's audio cannot be isolated, so all system audio is being recorded.");
        AutomationProperties.SetName(MicMeter, Loc.T("Mic level"));
        AutomationProperties.SetName(SystemMeter, Loc.T("System audio level"));
        // Again whenever the row or one of its columns changes width (the clock, the state, "Catch up", the language).
        foreach (var element in new FrameworkElement[] { StatusRow, ElapsedText, StateText, ChannelMeters, CatchUpButtonHost, LanguageHost })
        {
            element.SizeChanged += (_, _) => UpdateMetersLayout();
        }
        OpenSettingsLink.Click += (_, _) => OpenReasonSettings();
        // Each change renames the recording; the model keeps it trimmed, so the box is not bound to it.
        TitleBox.TextChanged += (_, _) => _model.SetRecordingTitle(TitleBox.Text);
        AcceptSuggestionButton.Click += (_, _) => _model.AcceptCalendarSuggestion();
        DismissSuggestionButton.Click += (_, _) => _model.DismissCalendarSuggestion();
        // Sets CatchUpRun at once, so its card shows in this update; in the Catch-up tab, which it selects.
        CatchUpButton.Click += (_, _) =>
        {
            _model.StartCatchUp();
            _model.RecordingTab = RecordingTab.CatchUp;
        };
        PanelTabsBar.SelectionChanged += (_, _) => OnPanelTabSelected();
        CatchUpSearchButton.Click += (_, _) => SetCatchUpSearch(CatchUpSearchButton.IsChecked == true);
        CatchUpSearchBox.TextChanged += (_, _) =>
        {
            _catchUpSearchOpenStates.Clear();
            UpdateCatchUps();
            ScrollCatchUpsToTop();
        };
        CatchUpSearchBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnCatchUpSearchKeyDown), handledEventsToo: true);
        ToolTipService.SetToolTip(PanelLanguageButton,
            Loc.T("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)"));
        PanelModelMenu.Opening += (_, _) => AIMenus.FillModelMenu(PanelModelMenu, _model.ProvidersModel, AIPurpose.CatchUp,
            () => _model.OpenSettings(SettingsSearch.Providers, ProvidersSettingsPage.CloudTab));
        PanelLanguageMenu.Opening += (_, _) =>
            AIMenus.FillLanguageMenu(PanelLanguageMenu, _model.ProvidersModel, _model.Settings.SummaryLanguage);
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _model.CatchUpTextChanged += OnCatchUpTextChanged;
            _model.ProvidersModel.Changed += OnProvidersChanged;
            Update();
            UpdateTitle();
            UpdateSuggestion();
            UpdateReason();
            UpdateLive();
            UpdateCatchUps();
            UpdateCatchUpButton();
            UpdateContent();
            UpdatePanelMenus();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.CatchUpTextChanged -= OnCatchUpTextChanged;
            _model.ProvidersModel.Changed -= OnProvidersChanged;
            _ticker.Run(false);
            _shownState = null;
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.RecordingSourceName):
                Update();
                break;
            case nameof(AppViewModel.Settings):
                UpdateReason();
                UpdateCatchUpButton();
                UpdatePanelMenus();
                break;
            case nameof(AppViewModel.RecordingLanguage):
            case nameof(AppViewModel.InstalledModelIds):
                UpdateReason();
                UpdateCatchUpButton();
                break;
            case nameof(AppViewModel.RecordingTab):
                UpdateContent();
                break;
            case nameof(AppViewModel.RecordingCaptions):
                UpdateLive();
                UpdateCatchUpButton();
                break;
            case nameof(AppViewModel.CatchUpRun):
            case nameof(AppViewModel.RecordingCatchUps):
                UpdateCatchUps();
                UpdateCatchUpButton();
                break;
            case nameof(AppViewModel.RecordingTitle):
                UpdateTitle();
                break;
            case nameof(AppViewModel.CalendarSuggestion):
                UpdateSuggestion();
                break;
        }
    }

    /// <summary>
    /// A title set elsewhere (the card, the calendar suggestion, the event at start) replaces the text unless only
    /// surrounding whitespace differs, so typing keeps its caret and trailing spaces (macOS <c>titleField</c>).
    /// </summary>
    private void UpdateTitle()
    {
        var title = _model.RecordingTitle ?? "";
        if (TitleBox.Text.Trim() == title) return;
        TitleBox.Text = title;
        TitleBox.SelectionStart = title.Length;
    }

    /// <summary>"Rename to "…"?" while the model suggests a calendar event.</summary>
    private void UpdateSuggestion()
    {
        if (_model.CalendarSuggestion is { } @event) SuggestionBar.Message = Loc.Format("Rename to \"%@\"?", @event.Title);
        SuggestionBar.IsOpen = _model.CalendarSuggestion is not null;
    }

    private void Update()
    {
        var snapshot = _model.RecordingSnapshot;
        var recording = snapshot.State != RecordingEngine.State.Idle;
        var paused = snapshot.State == RecordingEngine.State.Paused;
        RecordingIcon.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PausedIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        RecordingCard.SetText(ElapsedText, ConversationText.Clock(recording ? snapshot.Elapsed : 0));
        RecordingCard.SetText(StateText, Loc.T(paused ? "Paused" : "Recording"));
        ChannelMeters.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;

        GlobalTapWarning.IsOpen = recording && snapshot.GlobalTapFallback;
        SilentTapBanner.IsOpen = recording && snapshot.SilentTapSuspected;
        var silentTap = Loc.Format(
            "EchoDiar hears nothing from %@. Pick another source or check that the app is playing.",
            _model.RecordingSourceName ?? Loc.T("the app"));
        if (SilentTapBanner.Message != silentTap) SilentTapBanner.Message = silentTap;

        var live = recording && !paused;
        _ticker.Run(live);
        // The snapshot changes at 15 Hz (the elapsed time): the wave is redrawn here only when the state changes.
        if (snapshot.State == _shownState) return;
        _shownState = snapshot.State;
        if (!live) Array.Clear(_history);
        UpdateWave(paused);
    }

    /// <summary>The live transcript while the recording has a transcriber, else the wave and why.</summary>
    private void UpdateLive()
    {
        var live = _model.RecordingCaptions is not null;
        LiveHost.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        NoLivePanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The right panel's tab (macOS <c>RecordingDetailView</c> <c>sidePanel</c>): the catch-ups or the chat. The chat is
    /// made when its tab shows, so its question takes the focus, and dropped when the other one does (macOS: only the
    /// selected tab's view). Back on the catch-ups, the newest is in view.
    /// </summary>
    private void UpdateContent()
    {
        var chat = _model.RecordingTab == RecordingTab.Chat;
        var catchUpsShown = CatchUpPanel.Visibility == Visibility.Visible;
        CatchUpPanel.Visibility = Shown(!chat);
        CatchUpSearchButton.Visibility = Shown(!chat);
        if (!chat)
        {
            ChatHost.Child = null;
            if (!catchUpsShown) ScrollCatchUpsToTop();
        }
        else if (ChatHost.Child is null)
        {
            ChatHost.Child = new LiveChatView(_model) { Framed = false };
        }
        var item = chat ? ChatPanelTab : CatchUpPanelTab;
        if (PanelTabsBar.SelectedItem != item) PanelTabsBar.SelectedItem = item;
    }

    /// <summary>A tab picked in the right panel; only a pick that differs from the model's tab is written.</summary>
    private void OnPanelTabSelected()
    {
        if (PanelTabsBar.SelectedItem is not { } item) return;
        var picked = item == ChatPanelTab ? RecordingTab.Chat : RecordingTab.CatchUp;
        if (picked != _model.RecordingTab) _model.RecordingTab = picked;
    }

    /// <summary>
    /// The right panel's catch-up model (<see cref="AIPurpose.CatchUp"/>) and answer language labels (macOS
    /// <c>AIModelMenu</c> and <c>AILanguageMenu</c>), as the conversation's AI panel shows them.
    /// </summary>
    private void UpdatePanelMenus()
    {
        var title = AIMenus.ModelTitle(_model.ProvidersModel, AIPurpose.CatchUp);
        if (PanelModelText.Text != title)
        {
            PanelModelText.Text = title;
            AutomationProperties.SetName(PanelModelButton, title);
        }
        var language = LanguageOptions.ShortCode(_model.Settings.SummaryLanguage);
        if (PanelLanguageText.Text != language) PanelLanguageText.Text = language;
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateReason() => RecordingCard.SetText(ReasonText, NoCaptionsReason(_model));

    /// <summary>
    /// The providers changed: which one "Catch up" runs on (and the panel's model), the saved cards' provider names, and
    /// the default cloud engine the reason names.
    /// </summary>
    private void OnProvidersChanged()
    {
        UpdateReason();
        UpdateCatchUps();
        UpdateCatchUpButton();
        UpdatePanelMenus();
    }

    /// <summary>
    /// "Catch up" is off with the reason as its host's tooltip (a disabled button shows none), else its help, which says
    /// where the data goes, as the button's own tooltip, so it shows on keyboard focus too (macOS <c>CatchUpButton</c>).
    /// </summary>
    private void UpdateCatchUpButton()
    {
        var reason = _model.CatchUpDisabledReason;
        CatchUpButton.IsEnabled = reason is null;
        var tip = reason ?? _model.CatchUpHelp;
        if (tip == _catchUpTip) return;
        _catchUpTip = tip;
        ToolTipService.SetToolTip(CatchUpButtonHost, reason);
        ToolTipService.SetToolTip(CatchUpButton, reason is null ? tip : null);
        AutomationProperties.SetHelpText(CatchUpButton, tip);
    }

    /// <summary>
    /// The cards, newest first: the run at the top, then the saved catch-ups; each kept by id (the run's card becomes
    /// its saved catch-up's), the newest open unless clicked otherwise. A new card, or the run's failure, scrolls to the
    /// top. A hint to press Catch up while there are none. With a search term, only the catch-ups whose text contains it
    /// (and the run, so its Stop stays), the term highlighted; one or two found open, more keep their open state.
    /// </summary>
    private void UpdateCatchUps()
    {
        List<CatchUpCardState> states = [];
        var run = _model.CatchUpRun;
        var saved = _model.RecordingCatchUps;
        if (run is not null) states.Add(CatchUpCardState.From(run));
        if (saved.Count > 0)
        {
            var providers = _model.ProvidersModel.AIProviders;
            // The run's catch-up is listed once saved, a moment before the run is cleared: its card shows it once.
            foreach (var catchUp in saved.Reverse())
            {
                if (catchUp.Id != run?.JobId) states.Add(CatchUpCardState.From(catchUp, providers));
            }
        }
        var term = CatchUpSearchTerm;
        var matches = states.Where(s => term.Length > 0 && SearchText.Contains(s.Text, term)).Select(s => s.Id).ToHashSet();
        List<CatchUpCard> cards = [];
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (!_catchUpCards.TryGetValue(state.Id, out var card))
            {
                card = new CatchUpCard();
                var id = state.Id;
                card.ToggleRequested += () => ToggleCatchUp(id);
                card.StopRequested += _model.StopCatchUp;
                _catchUpCards[id] = card;
            }
            var normal = _catchUpOpenStates.TryGetValue(state.Id, out var open) ? open : i == 0;
            card.IsOpen = term.Length == 0 ? normal
                : _catchUpSearchOpenStates.TryGetValue(state.Id, out var searchOpen) ? searchOpen
                : (matches.Count <= 2 && matches.Contains(state.Id)) || normal;
            card.Highlight = term;
            card.Show(state);
            if (term.Length == 0 || matches.Contains(state.Id) || state.Running) cards.Add(card);
        }
        foreach (var gone in _catchUpCards.Keys.Where(key => !states.Any(s => s.Id == key)).ToList())
        {
            _catchUpCards.Remove(gone);
            _catchUpOpenStates.Remove(gone);
        }
        _runCard = run is not null ? _catchUpCards[run.JobId] : null;
        if (!CatchUpCards.Children.SequenceEqual<UIElement>(cards))
        {
            CatchUpCards.Children.Clear();
            foreach (var card in cards) CatchUpCards.Children.Add(card);
        }
        CatchUpScroller.Visibility = Shown(cards.Count > 0);
        CatchUpEmptyText.Visibility = Shown(cards.Count == 0);
        RecordingCard.SetText(CatchUpEmptyText, states.Count == 0
            ? Loc.T("No catch-up yet. Press Catch up to summarize the meeting so far.")
            : Loc.Format("No catch-up contains \"%@\".", term));
        var shown = (saved.Count, run?.JobId, run is { IsRunning: false });
        if (shown != _scrolledCatchUps)
        {
            _scrolledCatchUps = shown;
            ScrollCatchUpsToTop();
        }
    }

    /// <summary>The run's text or status changed (at most every 120 ms): only its card, whose Markdown is rendered again.</summary>
    private void OnCatchUpTextChanged()
    {
        if (_model.CatchUpRun is not { } run || _runCard is not { } card) return;
        card.Show(CatchUpCardState.From(run));
    }

    /// <summary>The newest catch-up, at the top, into view once laid out.</summary>
    private void ScrollCatchUpsToTop() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (!_model.IsClosing) CatchUpScroller.ChangeView(null, 0, null, disableAnimation: true);
    });

    private void ToggleCatchUp(Guid id)
    {
        if (!_catchUpCards.TryGetValue(id, out var card)) return;
        card.IsOpen = !card.IsOpen;
        (CatchUpSearchTerm.Length > 0 ? _catchUpSearchOpenStates : _catchUpOpenStates)[id] = card.IsOpen;
    }

    /// <summary>The catch-up search's term, trimmed; empty while the field is hidden.</summary>
    private string CatchUpSearchTerm =>
        CatchUpSearchBox.Visibility == Visibility.Visible ? CatchUpSearchBox.Text.Trim() : "";

    /// <summary>
    /// Shows the search field, sliding in under the header and focused, or hides it and clears the query (macOS
    /// <c>setCatchUpSearch</c>); without the slide when Windows animations are off.
    /// </summary>
    private void SetCatchUpSearch(bool shown)
    {
        CatchUpSearchButton.IsChecked = shown;
        if (!shown)
        {
            CatchUpSearchBox.Visibility = Visibility.Collapsed;
            CatchUpSearchBox.Text = "";
            UpdateCatchUps();
            return;
        }
        CatchUpSearchBox.TranslationTransition = null;
        CatchUpSearchBox.OpacityTransition = null;
        if (UISettings.AnimationsEnabled)
        {
            CatchUpSearchBox.Translation = new Vector3(0, -8, 0);
            CatchUpSearchBox.Opacity = 0;
        }
        CatchUpSearchBox.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
        {
            CatchUpSearchBox.TranslationTransition = new Vector3Transition();
            CatchUpSearchBox.OpacityTransition = new ScalarTransition();
            CatchUpSearchBox.Translation = Vector3.Zero;
            CatchUpSearchBox.Opacity = 1;
            CatchUpSearchBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Esc clears the search, then hides it (as macOS).</summary>
    private void OnCatchUpSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        if (CatchUpSearchBox.Text.Length > 0) CatchUpSearchBox.Text = "";
        else SetCatchUpSearch(false);
    }

    /// <summary>
    /// Why the recording has no live transcript, with the cloud live options when the default engine is a cloud one
    /// (macOS <c>noCaptionsReason(model:)</c>): the live source is off, or
    /// <see cref="AppViewModel.CurrentCloudLiveBlock"/> says what is in the way; also why "Catch up" is off.
    /// </summary>
    internal static string NoCaptionsReason(AppViewModel model)
    {
        var settings = model.Settings;
        if (!settings.AutoTranscribe || !settings.TranscribeWhileRecording || model.DefaultCloudConfig is not { } config)
            return NoCaptionsReason(settings);
        var name = config.DisplayName;
        return model.CurrentCloudLiveBlock switch
        {
            CloudLiveBlock.NoLiveModel =>
                Loc.T("No installed live model supports this recording's language. Download one in Settings › AI Providers › Local AI."),
            CloudLiveBlock.RealtimeUnsupported =>
                Loc.Format("%@ has no realtime transcription. You can show a live transcript with a local model in Settings.", name),
            CloudLiveBlock.CloudConsentPending =>
                Loc.Format("%@ realtime starts once sending audio to %@ (and to its speaker diarizer, if any) is confirmed. Confirm it in a conversation first.", name, name),
            CloudLiveBlock.RealtimeConsentPending =>
                Loc.Format("%@ realtime is not confirmed yet. Turn it on in Settings to confirm it.", name),
            _ => AppViewModel.RealtimeAvailable(config)
                ? Loc.Format("%@ transcribes after Stop. You can show a live transcript with a local model or %@ realtime in Settings.", name, name)
                : Loc.Format("%@ transcribes after Stop. You can show a live transcript with a local model in Settings.", name),
        };
    }

    /// <summary>
    /// Why there is no live transcript (the conditions of AppViewModel.AttachRecordingTranscriberAsync, read from the
    /// settings); also why "Catch up" is off with a local default engine. The last one names the Windows engines (macOS: WhisperKit and Parakeet).
    /// </summary>
    internal static string NoCaptionsReason(AppSettings settings) =>
        !settings.AutoTranscribe
            ? Loc.T("Transcription is off. Turn on Transcribe automatically in Settings › Recording.")
            : !settings.TranscribeWhileRecording
                ? Loc.T("Transcribe while recording is off. Turn it on in Settings › Transcription.")
                : Loc.T("This engine transcribes after Stop. Whisper and Parakeet models also transcribe while recording.");

    /// <summary>The Settings page the reason names (macOS <c>openReasonSettings</c>).</summary>
    private void OpenReasonSettings()
    {
        var settings = _model.Settings;
        if (!settings.AutoTranscribe) _model.OpenSettings(SettingsSearch.Recording, RecordingSettingsPage.AfterRecordingTab);
        else if (!settings.TranscribeWhileRecording) _model.OpenSettings(SettingsSearch.Transcription);
        else _model.OpenLocalAI(ProvidersSettingsPage.TranscriptionGroup);
    }

    /// <summary>
    /// The meters side by side where the room the other columns leave holds them at their full width (macOS
    /// <c>ChannelMeters</c>' ViewThatFits), else mic over system, so "Catch up" and the language are never squeezed. The
    /// labels are measured at their text's width in both layouts, so the choice does not depend on the current one.
    /// </summary>
    private void UpdateMetersLayout()
    {
        if (ChannelMeters.Visibility != Visibility.Visible || StatusRow.ActualWidth <= 0) return;
        var columns = StatusRow.ColumnDefinitions;
        var metersColumn = Grid.GetColumn(ChannelMeters);
        var others = 0.0;
        for (var i = 0; i < columns.Count; i++)
        {
            if (i != metersColumn && columns[i].Width.IsAuto) others += columns[i].ActualWidth;
        }
        var room = StatusRow.ActualWidth - others - (columns.Count - 1) * StatusRow.ColumnSpacing;
        var rowWidth = MicLabel.DesiredSize.Width + SystemLabel.DesiredSize.Width
            + 2 * (MicMeter.ColumnSpacing + RowBarWidth) + RowMetersSpacing;
        ApplyMetersLayout(room >= rowWidth);
    }

    private void ApplyMetersLayout(bool inRow)
    {
        if (inRow == _metersInRow) return;
        _metersInRow = inRow;
        ChannelMeters.Orientation = inRow ? Orientation.Horizontal : Orientation.Vertical;
        ChannelMeters.Spacing = inRow ? RowMetersSpacing : StackedMetersSpacing;
        ChannelMeters.Width = inRow ? double.NaN : StackedMetersWidth;
        var labelMinWidth = inRow ? 0 : StackedLabelMinWidth;
        MicLabelColumn.MinWidth = labelMinWidth;
        SystemLabelColumn.MinWidth = labelMinWidth;
        var barWidth = inRow ? new GridLength(RowBarWidth) : new GridLength(1, GridUnitType.Star);
        MicBarColumn.Width = barWidth;
        SystemBarColumn.Width = barWidth;
    }

    private void OnTick()
    {
        var mic = _model.MicLevel;
        var system = _model.SystemLevel;
        MicFill.Width = MicTrack.ActualWidth * mic;
        SystemFill.Width = SystemTrack.ActualWidth * system;
        SetPercent(MicMeter, mic, ref _micPercent);
        SetPercent(SystemMeter, system, ref _systemPercent);
        // The live transcript hides the wave.
        if (!UISettings.AnimationsEnabled || NoLivePanel.Visibility != Visibility.Visible) return;
        // The louder channel's peak; a fall decays from the last bar so the wave does not flicker.
        Array.Copy(_history, 1, _history, 0, BarCount - 1);
        _history[^1] = Math.Max(Math.Max(mic, system), _history[^2] * 0.8);
        UpdateWave(paused: false);
    }

    /// <summary>
    /// Flat and grey while paused; a still shape when animations are off; else the last peaks, newest on the right. The
    /// colours come from styles with ThemeResources, so a theme switch repaints the bars even while they stand still.
    /// </summary>
    private void UpdateWave(bool paused)
    {
        var still = !UISettings.AnimationsEnabled;
        var style = (Style)Resources[paused ? "PausedWaveBarStyle" : "WaveBarStyle"];
        for (var i = 0; i < BarCount; i++)
        {
            var height = paused ? 0 : still ? 0.2 + 0.4 * Math.Abs(Math.Sin(i * 0.5)) : _history[i];
            _bars[i].Height = Math.Max(4, WaveHeight * height);
            if (_bars[i].Style != style) _bars[i].Style = style;
        }
    }

    /// <summary>The meter's value for screen readers ("42 percent"), set when the whole percent changes.</summary>
    private static void SetPercent(FrameworkElement meter, double level, ref int last)
    {
        var percent = (int)Math.Round(level * 100);
        if (percent == last) return;
        last = percent;
        AutomationProperties.SetItemStatus(meter, Loc.Format("%lld percent", percent));
    }
}
