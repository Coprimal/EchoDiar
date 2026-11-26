using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using EchoDiar.AI;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The open conversation's AI panel beside its transcript, with its Summary | Chat tabs in the header inside its card,
/// above a line (macOS AIPanelView.swift, with its AIModelMenu, AILanguageMenu, AISetupHints and WebLinkConfirmation);
/// the tab is <see cref="AppViewModel.DetailTab"/>. The state is <see cref="AIPanelViewModel"/>; the jobs run in
/// <see cref="AppViewModel"/>, one per conversation, so one goes on when the page changes and the conversation's other
/// tab shows it as busy, with Stop. Answers are Markdown rendered by <see cref="MarkdownView"/> (at most every 120 ms
/// while streaming, as the model batches the text, and once more when saved); <c>[mm:ss]</c> citations call the seek
/// action <see cref="ConversationView"/> passes, web links open once their destination is confirmed. The Catch-up
/// tab, there only with catch-ups, shows those written while recording (<see cref="CatchUpCard"/>, read-only).
/// Attach when the page shows, Detach when it goes.
/// </summary>
public sealed partial class AIPanelView : UserControl
{
    /// <summary>How long the loading state shows without its ring: a ring shown for a frame or two reads as a flicker.</summary>
    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);

    private static readonly UISettings UISettings = new();

    /// <summary>
    /// The tabs' icons in a narrow header (macOS <c>tabIcon</c>): left-aligned lines, a bulleted list (as the
    /// recording's Catch-up tab) and a chat bubble.
    /// </summary>
    private const string SummaryTabGlyph = "\uE8E4";
    private const string CatchUpTabGlyph = "\uE8FD";
    private const string ChatTabGlyph = "\uE8BD";

    /// <summary>The collapse button's chevron (macOS <c>PanelCollapseButton</c>): right beside the transcript, down under it.</summary>
    private const string CollapseGlyph = "\uE76C";
    private const string CollapseStackedGlyph = "\uE70D";

    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _focusTimer;

    /// <summary>Fades the loading ring in after <see cref="SpinnerDelay"/>.</summary>
    private readonly DispatcherQueueTimer _spinnerTimer;

    private AppViewModel? _model;
    private AIPanelViewModel? _panel;
    private Action<double>? _seek;
    private DetailTab _tab = DetailTab.Summary;

    /// <summary>What was rendered last, so an update that changes nothing renders nothing.</summary>
    private string? _summaryRendered;
    private (IReadOnlyList<ChatLog.Message> Chat, bool HasJob, string? Question)? _chatRendered;
    private string? _streamRendered;
    private string? _historyKey;
    /// <summary>Each earlier chat's messages by id, read while the History search shows; null until read.</summary>
    private Dictionary<Guid, string>? _archiveTexts;
    /// <summary>The earlier chats <see cref="_archiveTexts"/> is (being) read for: other chats are read again.</summary>
    private IReadOnlyList<ChatArchive.Entry>? _archiveTextsFor;

    /// <summary>The streamed answer at the end of the messages: its status row, or its text once the first arrives.</summary>
    private StackPanel? _streamSlot;

    /// <summary><see cref="HasLoaded"/> as last shown: the tab's pane and footer instead of the loading ring.</summary>
    private bool _loadedSeen;

    /// <summary>
    /// An explicit switch to Chat (a tab click, Ctrl+I) before the files were read: the question takes the cursor once
    /// they are. Never set merely because the panel opens on Chat: ↑/↓ through the cards and Space must keep working.
    /// </summary>
    private bool _focusPending;

    /// <summary>The catch-up cards by id, kept while shown, so a card's Markdown is not rendered again.</summary>
    private readonly Dictionary<Guid, CatchUpCard> _catchUpCards = [];

    /// <summary>The open state the user gave cards with a click; the others: the newest open, the older ones collapsed.</summary>
    private readonly Dictionary<Guid, bool> _catchUpOpenStates = [];

    /// <summary>Whether the rail's tabs as last set include Catch-up; null before they are set.</summary>
    private bool? _railCatchUp;

    public AIPanelView()
    {
        InitializeComponent();
        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(2);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;
        _focusTimer = DispatcherQueue.CreateTimer();
        _focusTimer.Interval = TimeSpan.FromMilliseconds(50);
        _focusTimer.IsRepeating = false;
        _focusTimer.Tick += (_, _) =>
        {
            // Not while a sidebar card is being renamed: its field keeps the cursor (macOS c45b593).
            if (_model is { RenamingConversation: null } && _tab == DetailTab.Chat && QuestionBox.IsEnabled)
            {
                QuestionBox.Focus(FocusState.Programmatic);
            }
        };
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

        ToolTipService.SetToolTip(LanguageButton,
            Loc.T("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)"));
        HeaderRow.SizeChanged += (_, _) => FitHeader();
        // The menus and buttons exactly as tall as the tabs' track (macOS MainView.headerHeight), again when the tabs
        // change to their icons; their icons and names stay centred.
        PanelTabsTrack.SizeChanged += (_, _) =>
        {
            foreach (var child in HeaderMenus.Children)
                if (child is FrameworkElement element) element.Height = PanelTabsTrack.ActualHeight;
        };
        PanelTabsBar.SelectionChanged += (_, _) => OnPanelTabSelected();
        VersionMenu.Opening += (_, _) => BuildVersionMenu();
        ModelMenu.Opening += (_, _) => BuildModelMenu();
        LanguageMenu.Opening += (_, _) => BuildLanguageMenu();
        DataNotesFlyout.Opening += (_, _) => BuildDataNotes();
        BusyStopButton.Click += (_, _) => _panel?.Stop();
        SummaryStopButton.Click += (_, _) => _panel?.Stop();
        AskStopButton.Click += (_, _) => _panel?.Stop();
        SummarizeButton.Click += (_, _) => _panel?.Summarize();
        SendButton.Click += (_, _) => Ask();
        SummaryCopyButton.Click += (_, _) => CopyPrompt();
        FreshChatButton.Click += (_, _) => _panel?.StartFreshChat();
        HistoryButton.Click += (_, _) => ShowHistory(HistoryButton.IsChecked == true);
        CollapseButton.Click += (_, _) =>
        {
            if (_model is { IsClosing: false } model) model.AIPanelCollapsed = true;
        };
        CollapsedRail.TabInvoked += OnRailTabInvoked;
        CollapsedRail.ExpandInvoked += OnRailExpandInvoked;
        CloseHistoryButton.Click += (_, _) => ShowHistory(false);
        ClearHistoryButton.Click += (_, _) => _ = ClearHistoryAsync();
        HistorySearchButton.Click += (_, _) => SetHistorySearch(!SearchingHistory);
        HistorySearchBox.TextChanged += (_, _) => UpdateHistory();
        HistorySearchBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnHistorySearchKeyDown), handledEventsToo: true);
        QuestionBox.PreviewKeyDown += OnQuestionKeyDown;
        QuestionBox.TextChanged += (_, _) => UpdateActions();
    }

    /// <summary>The first read of the conversation's files has returned; until then the header shows over the loading ring.</summary>
    private bool HasLoaded => _panel?.HasLoaded == true;

    /// <summary>
    /// The rail in place of the card while <paramref name="collapsed"/> (the card only hides: the chat, the question and
    /// the loads stay), a row along the bottom when <paramref name="stacked"/>; the collapse button's chevron toward
    /// where the panel folds. Set by <see cref="ConversationView"/>, which folds the pane's column (row) too.
    /// </summary>
    public void ShowCollapsed(bool collapsed, bool stacked)
    {
        PanelCard.Visibility = Shown(!collapsed);
        CollapsedRail.Visibility = Shown(collapsed);
        CollapsedRail.Vertical = stacked;
        CollapseIcon.Glyph = stacked ? CollapseStackedGlyph : CollapseGlyph;
    }

    /// <summary>
    /// A tab of the rail: the panel opens on it (macOS <c>expand</c>); Chat as Ctrl+I, with the cursor in the question.
    /// </summary>
    private void OnRailTabInvoked(object tab)
    {
        if (_model is not { IsClosing: false } model || tab is not DetailTab picked) return;
        if (picked == DetailTab.Chat)
        {
            model.RequestChat();
            return;
        }
        model.DetailTab = picked;
        model.AIPanelCollapsed = false;
    }

    /// <summary>The rail's chevron: the panel opens on its first tab, Summary.</summary>
    private void OnRailExpandInvoked() => OnRailTabInvoked(DetailTab.Summary);

    /// <summary>Shows conversation <paramref name="id"/>; <paramref name="seek"/> opens the transcript at a citation's time.</summary>
    public void Attach(AppViewModel model, Guid id, Action<double> seek)
    {
        Detach();
        _model = model;
        _seek = seek;
        _panel = new AIPanelViewModel(model, id);
        _panel.Changed += Update;
        _model.PropertyChanged += OnModelChanged;
        _model.AIJobTextChanged += OnJobTextChanged;
        _panel.Attach();
        _tab = model.DetailTab;
        UpdateTab();
        ShowLoading(true);
        Update();
    }

    public void Detach()
    {
        _toastTimer.Stop();
        _focusTimer.Stop();
        _spinnerTimer.Stop();
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.AIJobTextChanged -= OnJobTextChanged;
        }
        if (_panel is not null)
        {
            _panel.Changed -= Update;
            _panel.Detach();
        }
        _model = null;
        _panel = null;
        _seek = null;
        _summaryRendered = null;
        _chatRendered = null;
        _streamRendered = null;
        _historyKey = null;
        _archiveTexts = null;
        _archiveTextsFor = null;
        _streamSlot = null;
        _loadedSeen = false;
        _focusPending = false;
        _catchUpCards.Clear();
        _catchUpOpenStates.Clear();
        CatchUpCards.Children.Clear();
    }

    /// <summary>
    /// A switch to Chat while the panel shows, or Ctrl+I on Chat (<see cref="AppViewModel.ChatFocusRequests"/>), puts the
    /// cursor in the question once the files are read (macOS: on a change of the tab only, never when it opens on Chat).
    /// </summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not { IsClosing: false } model) return;
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.DetailTab) when model.DetailTab != _tab:
                _tab = model.DetailTab;
                UpdateTab();
                if (_tab == DetailTab.Chat) FocusQuestionOnceLoaded();
                Update();
                break;
            case nameof(AppViewModel.ChatFocusRequests) when _tab == DetailTab.Chat:
                FocusQuestionOnceLoaded();
                break;
        }
    }

    /// <summary>The streamed text or status of a job changed: this conversation's only redraws its answer.</summary>
    private void OnJobTextChanged(Guid? key)
    {
        if (_model is not { IsClosing: false } || _panel is null || key != _panel.ConversationId) return;
        UpdateSummary();
        UpdateChat();
    }

    /// <summary>
    /// The tab selected in the header, its pane and footer: Summary's actions, Catch-up's cards without a footer (the
    /// header's menus hidden but keeping their room), or Chat's question row with a fresh chat and History in the
    /// header. Leaving Chat closes the History panel.
    /// </summary>
    private void UpdateTab()
    {
        var chat = _tab == DetailTab.Chat;
        var catchUp = _tab == DetailTab.CatchUp;
        var item = _tab switch
        {
            DetailTab.Chat => ChatPanelTab,
            DetailTab.CatchUp => CatchUpPanelTab,
            _ => SummaryPanelTab,
        };
        if (PanelTabsBar.SelectedItem != item) PanelTabsBar.SelectedItem = item;
        SummaryPane.Visibility = Shown(_tab == DetailTab.Summary);
        CatchUpPane.Visibility = Shown(catchUp);
        ChatPane.Visibility = Shown(chat);
        // The catch-ups are read-only: no footer.
        Footer.Visibility = Shown(ContentHost.Visibility == Visibility.Visible && !catchUp);
        SummaryActions.Visibility = Shown(!chat);
        QuestionRow.Visibility = Shown(chat);
        FreshChatButton.Visibility = Shown(chat);
        HistoryButton.Visibility = Shown(chat);
        foreach (var control in new Control[] { ModelButton, LanguageButton, DataNotesButton }) Reserve(control, hidden: catchUp);
        if (!chat) ShowHistory(false);
        UpdateCatchUps();
        FitHeader();
    }

    /// <summary>
    /// The rail's tabs, the header's: Summary, Catch-up with catch-ups, Chat. Set again only when that changes, as the
    /// rail builds its buttons again.
    /// </summary>
    private void UpdateRailTabs(bool catchUp)
    {
        if (catchUp == _railCatchUp) return;
        _railCatchUp = catchUp;
        List<(object Tab, string Glyph, string Title)> tabs = [(DetailTab.Summary, SummaryTabGlyph, Loc.T("Summary"))];
        if (catchUp) tabs.Add((DetailTab.CatchUp, CatchUpTabGlyph, Loc.T("Catch-up")));
        tabs.Add((DetailTab.Chat, ChatTabGlyph, Loc.T("Chat")));
        CollapsedRail.SetTabs(tabs);
    }

    /// <summary>
    /// Hidden but keeping its room (macOS <c>hidden()</c>), so the header's step does not change with the tab: not
    /// drawn, clicked, tabbed to or read out.
    /// </summary>
    private static void Reserve(Control control, bool hidden)
    {
        control.Opacity = hidden ? 0 : 1;
        control.IsHitTestVisible = !hidden;
        control.IsTabStop = !hidden;
        AutomationProperties.SetAccessibilityView(control, hidden ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
    }

    /// <summary>A tab picked in the header; only a pick that differs from the model's tab is written.</summary>
    private void OnPanelTabSelected()
    {
        if (_model is not { IsClosing: false } model || PanelTabsBar.SelectedItem is not { } item) return;
        var picked = item == ChatPanelTab ? DetailTab.Chat : item == CatchUpPanelTab ? DetailTab.CatchUp : DetailTab.Summary;
        if (picked != model.DetailTab) model.DetailTab = picked;
    }

    /// <summary>
    /// The header at once; until the files are read, the loading ring in place of the tab's pane and footer, faded in
    /// only after <see cref="SpinnerDelay"/> (macOS <c>DetailLoadingPane(framed: false)</c>).
    /// </summary>
    private void ShowLoading(bool loading)
    {
        _spinnerTimer.Stop();
        LoadingRing.IsActive = false;
        LoadingRing.Opacity = 0;
        LoadingRing.Visibility = Shown(loading);
        ContentHost.Visibility = Shown(!loading);
        Footer.Visibility = Shown(!loading && _tab != DetailTab.CatchUp);
        if (loading) _spinnerTimer.Start();
        Services.StartupTrace.Write($"conversation {_panel?.ConversationId} AI panel: {(loading ? "loading ring" : "content")} shown");
    }

    /// <summary>The question takes the cursor now if the files are read, else once they are (macOS: once loaded).</summary>
    private void FocusQuestionOnceLoaded()
    {
        _focusPending = !HasLoaded;
        if (!_focusPending) _focusTimer.Start();
    }

    /// <summary>Everything from the state: the panes, the error, the header's menus, the footer.</summary>
    private void Update()
    {
        if (_model is not { IsClosing: false } || _panel is null) return;
        if (_panel.HasLoaded && !_loadedSeen)
        {
            _loadedSeen = true;
            ShowLoading(false);
            if (_focusPending && _tab == DetailTab.Chat) FocusQuestionOnceLoaded();
        }
        // Without catch-ups there is no Catch-up tab: opened on it (the list's arrows keep the tab), it shows Summary
        // (macOS leaveEmptyCatchUpTab); the model's change updates everything.
        if (_panel.HasLoaded && _panel.CatchUps.Count == 0 && _model.DetailTab == DetailTab.CatchUp)
        {
            _model.DetailTab = DetailTab.Summary;
            return;
        }
        UpdateSummary();
        UpdateCatchUps();
        UpdateChat();
        UpdateHistory();
        var error = _panel.Error;
        ErrorText.Text = error ?? "";
        ToolTipService.SetToolTip(ErrorText, error);
        ErrorText.Visibility = Shown(error is not null);
        UpdateMenus();
        UpdateActions();
    }

    // Summary

    /// <summary>The summary being written, else the shown version with its menu; a note in the middle while there is none.</summary>
    private void UpdateSummary()
    {
        if (_panel is null) return;
        var job = _panel.SummaryJob;
        var shown = _panel.ShownSummary;
        var none = job is null && shown is null;
        SummaryEmptyText.Text = _panel.EmptyNote ?? Loc.T("No summary yet.");
        SummaryEmptyText.Visibility = Shown(none);
        SummaryContent.Visibility = Shown(!none);
        VersionButton.Visibility = Shown(job is null && shown is not null);
        if (shown is not null)
        {
            var label = _panel.VersionLabel(shown);
            VersionText.Text = label;
            AutomationProperties.SetName(VersionButton, label);
        }
        // Until the first text: a remote provider sends no status before it.
        var waiting = job is { Text.Length: 0 };
        SummaryStatusPanel.Visibility = Shown(waiting);
        SummaryStatusText.Text = _panel.JobStatus ?? Loc.T("Writing the summary…");
        var text = job is not null ? job.Text : shown?.Text ?? "";
        SummaryText.Visibility = Shown(!waiting && !none);
        if (text != _summaryRendered)
        {
            _summaryRendered = text;
            MarkdownView.Render(SummaryText, text, Links());
        }
    }

    /// <summary>
    /// The Catch-up tab, there only while the conversation has catch-ups, and its cards (macOS <c>catchUpPane</c>):
    /// newest first, the newest open and the others collapsed unless clicked otherwise, each with Copy; each kept by id.
    /// Built only while the tab shows, as macOS.
    /// </summary>
    private void UpdateCatchUps()
    {
        if (_panel is null) return;
        var catchUps = _panel.CatchUps;
        CatchUpPanelTab.Visibility = Shown(catchUps.Count > 0);
        UpdateRailTabs(catchUps.Count > 0);
        if (_tab != DetailTab.CatchUp) return;
        var providers = _panel.Providers.AIProviders;
        List<CatchUpCard> cards = [];
        for (var i = catchUps.Count - 1; i >= 0; i--)
        {
            var state = CatchUpCardState.From(catchUps[i], providers);
            if (!_catchUpCards.TryGetValue(state.Id, out var card))
            {
                card = new CatchUpCard(Links());
                var id = state.Id;
                card.ToggleRequested += () => ToggleCatchUp(id);
                _catchUpCards[id] = card;
            }
            card.IsOpen = _catchUpOpenStates.TryGetValue(state.Id, out var open) ? open : cards.Count == 0;
            card.Show(state);
            cards.Add(card);
        }
        foreach (var gone in _catchUpCards.Keys.Where(key => !catchUps.Any(c => c.Id == key)).ToList())
        {
            _catchUpCards.Remove(gone);
            _catchUpOpenStates.Remove(gone);
        }
        if (!CatchUpCards.Children.SequenceEqual<UIElement>(cards))
        {
            CatchUpCards.Children.Clear();
            foreach (var card in cards) CatchUpCards.Children.Add(card);
        }
    }

    private void ToggleCatchUp(Guid id)
    {
        if (!_catchUpCards.TryGetValue(id, out var card)) return;
        card.IsOpen = !card.IsOpen;
        _catchUpOpenStates[id] = card.IsOpen;
    }

    /// <summary>The kept summaries, newest first, each with its model, date and whether it was stopped.</summary>
    private void BuildVersionMenu()
    {
        VersionMenu.Items.Clear();
        if (_panel is null) return;
        var shown = _panel.ShownSummary;
        foreach (var version in _panel.Summaries.Reverse())
        {
            var details = _panel.VersionDetails(version);
            var title = Loc.Format("Version %lld", version.Number);
            var item = new RadioMenuFlyoutItem
            {
                Text = details.Length == 0 ? title : $"{title} · {details}",
                GroupName = "SummaryVersion",
                IsChecked = version.Number == shown?.Number,
            };
            var number = version.Number;
            item.Click += (_, _) => _panel?.ShowVersion(number);
            VersionMenu.Items.Add(item);
        }
    }

    // Chat

    /// <summary>
    /// The messages and the answer being written, rebuilt when the chat or the question changes; the streamed answer
    /// alone follows the job's updates. A note while there are none.
    /// </summary>
    private void UpdateChat()
    {
        if (_panel is null) return;
        var job = _panel.QuestionJob;
        var empty = _panel.Chat.Count == 0 && job is null;
        ChatEmptyText.Text = _panel.EmptyNote ?? Loc.T("Ask a question about this conversation below.");
        ChatEmptyText.Visibility = Shown(empty);
        ChatScroller.Visibility = Shown(!empty);
        var key = (_panel.Chat, job is not null, job?.Question);
        if (_chatRendered != key)
        {
            _chatRendered = key;
            _streamRendered = null;
            _streamSlot = null;
            MessagesPanel.Children.Clear();
            foreach (var message in _panel.Chat)
            {
                MessagesPanel.Children.Add(MessageView(message.Role, message.Text, message.Model, live: message.Live == true));
            }
            if (job is not null)
            {
                MessagesPanel.Children.Add(MessageView(ChatRole.User, job.Question ?? "", null));
                _streamSlot = new StackPanel();
                MessagesPanel.Children.Add(_streamSlot);
            }
            ScrollChatToEnd();
        }
        if (job is not null) UpdateStream(job);
    }

    /// <summary>
    /// A spinner with the status ("Queued" behind another in-app job) until the first text, then the answer so far with
    /// the model's badge.
    /// </summary>
    private void UpdateStream(AIJob job)
    {
        if (_streamSlot is null) return;
        var jobStatus = _panel?.JobStatus;
        var key = job.Text.Length == 0 ? "\u0001" + jobStatus : job.Text;
        if (key == _streamRendered) return;
        _streamRendered = key;
        _streamSlot.Children.Clear();
        if (job.Text.Length == 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = true });
            if (jobStatus is { } status)
            {
                row.Children.Add(new TextBlock
                {
                    Text = status, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Resources["CaptionStyle"],
                });
            }
            _streamSlot.Children.Add(row);
        }
        else
        {
            _streamSlot.Children.Add(MessageView(ChatRole.Assistant, job.Text, _panel?.CurrentModel, streaming: true));
        }
        ScrollChatToEnd();
    }

    /// <summary>
    /// A question in a bubble at the trailing edge; an answer's Markdown with its model's badge and Copy. A message of
    /// the live chat (asked while recording) shows a "Live" badge: under the question, before the answer's model badge.
    /// </summary>
    private UIElement MessageView(ChatRole role, string text, string? model, bool streaming = false, bool live = false)
    {
        if (role == ChatRole.User)
        {
            var bubble = new Border
            {
                Style = (Style)Resources["UserBubbleStyle"],
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            };
            if (!live) return bubble;
            var question = new StackPanel { Spacing = 6 };
            question.Children.Add(bubble);
            var liveBadge = Badge(Loc.T("Live"));
            liveBadge.HorizontalAlignment = HorizontalAlignment.Right;
            question.Children.Add(liveBadge);
            return question;
        }
        var answer = new StackPanel { Spacing = 6 };
        var markdown = new RichTextBlock();
        MarkdownView.Render(markdown, text, Links());
        answer.Children.Add(markdown);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (live) footer.Children.Add(Badge(Loc.T("Live")));
        if (model is { Length: > 0 }) footer.Children.Add(Badge(model));
        if (!streaming)
        {
            var copy = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Padding = new Thickness(6, 2, 6, 2),
                Content = new FontIcon { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 12, Glyph = "" },
            };
            AutomationProperties.SetName(copy, Loc.T("Copy"));
            ToolTipService.SetToolTip(copy, Loc.T("Copy the answer"));
            copy.Click += (_, _) => CopyText(text, Loc.T("Copied"));
            footer.Children.Add(copy);
        }
        if (footer.Children.Count > 0) answer.Children.Add(footer);
        return answer;
    }

    /// <summary>A caption in a capsule: an answer's model, or "Live" on a message of the live chat.</summary>
    private Border Badge(string text) => new()
    {
        Style = (Style)Resources["BadgeStyle"],
        Child = new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Resources["CaptionStyle"],
        },
    };

    /// <summary>Once laid out, so the new height is known.</summary>
    private void ScrollChatToEnd() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (_model is { IsClosing: false }) ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight, null, disableAnimation: true);
    });

    /// <summary>Enter sends; Shift+Enter starts a new line (the box accepts returns).</summary>
    private void OnQuestionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || ConversationListView.IsKeyDown(VirtualKey.Shift)) return;
        e.Handled = true;
        Ask();
    }

    private void Ask()
    {
        if (_panel is not null && _panel.Ask(QuestionBox.Text)) QuestionBox.Text = "";
    }

    // History

    /// <summary>Opens or closes the History panel (not while an answer streams: its rows are off then).</summary>
    private void ShowHistory(bool show)
    {
        if (!show && SearchingHistory) SetHistorySearch(false);
        HistoryPanel.Visibility = Shown(show);
        HistoryButton.IsChecked = show;
    }

    /// <summary>The History panel's search field shows.</summary>
    private bool SearchingHistory => HistorySearchBox.Visibility == Visibility.Visible;

    /// <summary>The History search's term, trimmed; empty while the field is hidden.</summary>
    private string HistorySearchTerm => SearchingHistory ? HistorySearchBox.Text.Trim() : "";

    /// <summary>
    /// Shows the History search field, focused, or hides it, clearing the query and the messages read for it (macOS
    /// <c>setHistorySearch</c>); the search button is accent-colored while it shows.
    /// </summary>
    private void SetHistorySearch(bool shown)
    {
        HistorySearchBox.Visibility = Shown(shown);
        if (shown)
        {
            HistorySearchIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            DispatcherQueue.TryEnqueue(() => HistorySearchBox.Focus(FocusState.Programmatic));
        }
        else
        {
            HistorySearchIcon.ClearValue(IconElement.ForegroundProperty);
            HistorySearchBox.Text = "";
            _archiveTexts = null;
            _archiveTextsFor = null;
        }
        UpdateHistory();
    }

    /// <summary>Esc clears the search, then hides it (as macOS).</summary>
    private void OnHistorySearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        if (HistorySearchBox.Text.Length > 0) HistorySearchBox.Text = "";
        else SetHistorySearch(false);
    }

    /// <summary>
    /// Each earlier chat's messages, read off the UI thread for the search (macOS <c>loadArchiveTexts</c>); dropped when
    /// the chats changed or the search closed meanwhile.
    /// </summary>
    private async Task LoadArchiveTextsAsync(string folder, IReadOnlyList<ChatArchive.Entry> archives)
    {
        _archiveTextsFor = archives;
        var texts = await Task.Run(() => ChatArchive.SearchTexts(folder));
        if (!ReferenceEquals(archives, _archiveTextsFor) || !SearchingHistory) return;
        _archiveTexts = texts;
        _historyKey = null;
        UpdateHistory();
    }

    /// <summary>Esc: closes the History panel when it is open; false when it was not.</summary>
    internal bool CloseHistory()
    {
        if (HistoryPanel.Visibility != Visibility.Visible) return false;
        ShowHistory(false);
        return true;
    }

    /// <summary>
    /// The open chat first, highlighted (its title, else its first question, "New chat" while empty), then the earlier
    /// chats, newest first: a click resumes one, Remove on hover deletes it; all off while an answer streams.
    /// </summary>
    private void UpdateHistory()
    {
        if (_panel is null) return;
        var term = HistorySearchTerm;
        if (term.Length > 0 && !ReferenceEquals(_panel.Archives, _archiveTextsFor) && _panel.Conversation?.Folder is { } folder)
            _ = LoadArchiveTextsAsync(folder, _panel.Archives);
        var busy = _panel.RunningQuestion is not null || _model?.ChatChanging == true;
        ClearHistoryButton.IsEnabled = _panel.Archives.Count > 0 && !busy;
        var title = _panel.ChatTitle ?? _panel.Chat.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? Loc.T("New chat");
        var key = string.Join("\u0001", new[] { title, _panel.Chat.Count.ToString(CultureInfo.InvariantCulture), busy.ToString(), term }
            .Concat(_panel.Archives.Select(a => $"{a.Id}\u0002{a.Title}\u0002{a.MessageCount}\u0002{a.ArchivedAt.ToUnixTimeSeconds()}")));
        if (key == _historyKey) return;
        _historyKey = key;
        HistoryList.Children.Clear();

        // While searching, the chats whose title or messages contain the term, the open one too.
        var shown = _panel.Archives.Where(a => ChatArchive.Matches(a.Title, _archiveTexts?.GetValueOrDefault(a.Id), term)).ToList();
        var currentShown = ChatArchive.Matches(_panel.ChatTitle ?? "", term.Length == 0 ? null : string.Join("\n", _panel.Chat.Select(m => m.Text)), term);
        if (currentShown) AddCurrentChatRow(title);
        foreach (var entry in shown) HistoryList.Children.Add(HistoryRow(entry, busy));
        if ((shown.Count == 0 && !currentShown) || _panel.Archives.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = shown.Count == 0 && !currentShown ? Loc.T("No matching chats") : Loc.T("No earlier chats"), Style = (Style)Resources["CaptionStyle"],
                // Inset as the header's title and the rows' text.
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 8, 8, 0),
            });
        }
    }

    /// <summary>The open chat, highlighted: its title, else its first question, "New chat" while empty.</summary>
    private void AddCurrentChatRow(string title)
    {
        if (_panel is null) return;
        var current = new StackPanel { Spacing = 2 };
        // The title may come from a model: plain text, never Markdown.
        current.Children.Add(new TextBlock { Text = title, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var count = _panel.Chat.Count;
        current.Children.Add(new TextBlock
        {
            Text = count == 0 ? Loc.T("Current") : Loc.Plural("Current · %lld messages", count),
            Style = (Style)Resources["CaptionStyle"],
        });
        HistoryList.Children.Add(new Border { Style = (Style)Resources["CurrentChatStyle"], Child = current });
    }

    /// <summary>An earlier chat: its title and when it was archived; Remove shows while the pointer or the focus is on it.</summary>
    private UIElement HistoryRow(ChatArchive.Entry entry, bool busy)
    {
        var text = new StackPanel { Spacing = 2 };
        // The title may come from a model: plain text, never Markdown.
        text.Children.Add(new TextBlock { Text = entry.Title, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = Loc.Plural("%@ · %lld messages", entry.MessageCount,
                entry.ArchivedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), entry.MessageCount),
            Style = (Style)Resources["CaptionStyle"],
        });
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 32, 6),
            Content = text,
            IsEnabled = !busy,
        };
        AutomationProperties.SetName(row, entry.Title);
        var id = entry.Id;
        row.Click += (_, _) =>
        {
            _panel?.Resume(id);
            ShowHistory(false);
        };
        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Content = new FontIcon { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 12, Glyph = "" },
            Opacity = 0,
            IsEnabled = !busy,
        };
        AutomationProperties.SetName(remove, Loc.T("Remove"));
        ToolTipService.SetToolTip(remove, Loc.T("Remove this chat"));
        remove.Click += (_, _) => _panel?.DeleteChat(id);
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(remove);
        var pointerOver = false;
        void Reveal() => remove.Opacity = pointerOver || row.FocusState != FocusState.Unfocused || remove.FocusState != FocusState.Unfocused ? 1 : 0;
        grid.PointerEntered += (_, _) =>
        {
            pointerOver = true;
            Reveal();
        };
        grid.PointerExited += (_, _) =>
        {
            pointerOver = false;
            Reveal();
        };
        row.GotFocus += (_, _) => Reveal();
        row.LostFocus += (_, _) => Reveal();
        remove.GotFocus += (_, _) => Reveal();
        remove.LostFocus += (_, _) => Reveal();
        return grid;
    }

    /// <summary>"Delete all earlier chats?": the open chat stays.</summary>
    private async Task ClearHistoryAsync()
    {
        if (_model?.Confirm is not { } confirm) return;
        var panel = _panel;
        if (await confirm(Loc.T("Delete all earlier chats?"), Loc.T("The open chat stays. This can't be undone."), Loc.T("Delete")))
        {
            if (ReferenceEquals(panel, _panel)) panel?.ClearChats();
        }
    }

    // Header and footer

    /// <summary>
    /// The panel's tab (macOS <c>purpose</c>): Chat's questions use the chat default, the Summary tab the summary
    /// default.
    /// </summary>
    private AIPurpose Purpose => _tab == DetailTab.Chat ? AIPurpose.Chat : AIPurpose.Summary;

    /// <summary>The model (of the tab's purpose) and language labels, the "i", the setup hints and the busy line.</summary>
    private void UpdateMenus()
    {
        if (_panel is null || _model is null) return;
        var providers = _panel.Providers;
        var current = providers.CurrentAIProvider(Purpose);
        var title = AIMenus.ModelTitle(providers, Purpose);
        ModelText.Text = title;
        AutomationProperties.SetName(ModelButton, title);
        var language = _model.Settings.SummaryLanguage;
        LanguageText.Text = LanguageOptions.ShortCode(language);
        // Shown by FitHeader, which hides it in the narrowest header.
        _hasDataNotes = DataNotes().Count > 0;
        FitHeader();

        SetupHints.Visibility = Shown(current is null);
        var windowsAI = AIMenus.WindowsAIHint(providers);
        WindowsAIHint.Text = windowsAI ?? "";
        WindowsAIHint.Visibility = Shown(current is null && windowsAI is not null);

        // This conversation's job of the other tab; other conversations' jobs run beside this one's.
        if (_panel.BusyElsewhere(_tab) is { } job)
        {
            var conversationTitle = _panel.Conversation?.Title ?? "";
            BusyText.Text = job.Kind == AIJobKind.Summary
                ? Loc.Format("Summarizing %@…", conversationTitle)
                : Loc.Format("Answering about %@…", conversationTitle);
            ToolTipService.SetToolTip(BusyText, BusyText.Text);
            BusyRow.Visibility = Visibility.Visible;
        }
        else
        {
            BusyRow.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The "i" has notes to show: hidden in the narrowest header only.</summary>
    private bool _hasDataNotes;

    /// <summary>What <see cref="FitHeader"/> last fitted: the width, the labels and whether the "i" shows.</summary>
    private (double Width, string Model, string Language, bool Notes)? _headerFitted;

    /// <summary>
    /// The header on one row, never wrapped (macOS <c>headerRow</c>): with both names when everything fits, else the
    /// language as its globe only, then the model as its chip only too, the names in their tooltips, then the tabs as
    /// their icons too, then also without the "i" and with narrower menus and buttons (<see cref="ApplyHeaderStep"/>).
    /// Chosen as if Chat's fresh chat and History showed, so a switch of tabs never changes the step. Decided from the
    /// natural widths, outside the layout pass (the header's SizeChanged, a name or a button that changed). Too narrow
    /// even then, the menus are clipped at the trailing edge.
    /// </summary>
    private void FitHeader()
    {
        var width = HeaderRow.ActualWidth;
        if (width <= 0) return;
        var key = (width, ModelText.Text, LanguageText.Text, _hasDataNotes);
        if (_headerFitted == key) return;
        _headerFitted = key;
        FreshChatButton.Visibility = Visibility.Visible;
        HistoryButton.Visibility = Visibility.Visible;
        AIMenus.FitRow(width, HeaderRow, PanelTabsTrack, HeaderMenus, 5, ApplyHeaderStep);
        var chat = _tab == DetailTab.Chat;
        FreshChatButton.Visibility = Shown(chat);
        HistoryButton.Visibility = Shown(chat);
        // Measured at their natural width above: the next layout pass measures them with the header's width again.
        HeaderRow.InvalidateMeasure();
    }

    /// <summary>
    /// One of <see cref="FitHeader"/>'s steps: 0 everything named, 1 the globe alone, 2 the chip alone too, 3 the tabs'
    /// icons too, 4 also no "i", 4 px less side padding in the tabs, the menus and the buttons (macOS 12 → 8 pt) and
    /// 6 px between the row's items instead of 8 (macOS <c>tight</c>), so the three tabs fit the panel's minimum width.
    /// </summary>
    private void ApplyHeaderStep(int step)
    {
        ShowHeaderLabels(model: step < 2, language: step < 1);
        AIMenus.ShowTabLabel(SummaryPanelTab, Loc.T("Summary"), SummaryTabGlyph, label: step < 3);
        AIMenus.ShowTabLabel(CatchUpPanelTab, Loc.T("Catch-up"), CatchUpTabGlyph, label: step < 3);
        AIMenus.ShowTabLabel(ChatPanelTab, Loc.T("Chat"), ChatTabGlyph, label: step < 3);
        var compact = step >= 4;
        DataNotesButton.Visibility = Shown(_hasDataNotes && !compact);
        foreach (var control in new Control[] { SummaryPanelTab, CatchUpPanelTab, ChatPanelTab, ModelButton, LanguageButton, FreshChatButton, HistoryButton, CollapseButton })
        {
            AIMenus.CompactPadding(control, compact);
        }
        var spacing = compact ? 6 : 8;
        HeaderRow.ColumnSpacing = spacing;
        // The menus' margins space them (a collapsed one keeps none).
        foreach (var control in new FrameworkElement[] { LanguageButton, DataNotesButton, FreshChatButton, HistoryButton, CollapseButton })
        {
            control.Margin = new Thickness(spacing, 0, 0, 0);
        }
    }

    /// <summary>The model's and the language's names beside their icons, or only in their tooltips.</summary>
    private void ShowHeaderLabels(bool model, bool language)
    {
        ModelText.Visibility = Shown(model);
        LanguageText.Visibility = Shown(language);
        AIMenus.TightenChevron(ModelButton, iconOnly: !model);
        AIMenus.TightenChevron(LanguageButton, iconOnly: !language);
        var help = Loc.T("Model for summaries and questions");
        ToolTipService.SetToolTip(ModelButton, model ? help : $"{help}: {ModelText.Text}");
        // The globe alone has no text to be read out (macOS accessibilityLabel("Language")).
        if (language) LanguageButton.ClearValue(AutomationProperties.NameProperty);
        else AutomationProperties.SetName(LanguageButton, $"{Loc.T("Language")}: {LanguageText.Text}");
    }

    /// <summary>Summarize, Re-summarize or Stop; Send or Stop; Copy as prompt (Summary), a fresh chat; each enabled when it can run.</summary>
    private void UpdateActions()
    {
        if (_panel is null || _model is null) return;
        var summaryRunning = _panel.RunningSummary is not null;
        SummaryStopButton.Visibility = Shown(summaryRunning);
        SummarizeButton.Visibility = Shown(!summaryRunning);
        SummarizeButton.Content = Loc.T(_panel.Summaries.Count == 0 ? "Summarize" : "Re-summarize");
        ToolTipService.SetToolTip(SummarizeButton,
            _panel.Summaries.Count == 0 ? null : Loc.T("Make a new summary; the earlier ones stay in the version menu"));
        SummarizeButton.IsEnabled = _panel.CanRun(AIPurpose.Summary);

        var questionRunning = _panel.RunningQuestion is not null;
        AskStopButton.Visibility = Shown(questionRunning);
        SendButton.Visibility = Shown(!questionRunning);
        SendButton.IsEnabled = _panel.CanRun(AIPurpose.Chat) && QuestionBox.Text.Trim().Length > 0;
        FreshChatButton.IsEnabled = _panel.Chat.Count > 0 && !questionRunning && !_model.ChatChanging;

        SummaryCopyButton.IsEnabled = _panel.Transcript is not null;
    }

    /// <summary>
    /// The providers and their models for the tab's purpose (<see cref="AIMenus.FillModelMenu"/>), then "Choose Models…"
    /// (Settings › AI Providers › Cloud, macOS <c>openSettings(tab: .providers, subTab: "cloud")</c>).
    /// </summary>
    private void BuildModelMenu()
    {
        if (_panel is null || _model is null)
        {
            ModelMenu.Items.Clear();
            return;
        }
        AIMenus.FillModelMenu(ModelMenu, _panel.Providers, Purpose,
            () => _model?.OpenSettings(Services.SettingsSearch.Providers, ProvidersSettingsPage.CloudTab));
    }

    /// <summary>The summary and answer languages (<see cref="AIMenus.FillLanguageMenu"/>).</summary>
    private void BuildLanguageMenu()
    {
        if (_panel is null || _model is null)
        {
            LanguageMenu.Items.Clear();
            return;
        }
        AIMenus.FillLanguageMenu(LanguageMenu, _panel.Providers, _model.Settings.SummaryLanguage);
    }

    /// <summary>
    /// Where the data goes with the tab's provider (macOS <c>dataNotes</c>): off this PC, and how far a CLI agent is
    /// contained. Empty when there is nothing to say (the "i" is then hidden).
    /// </summary>
    private List<(string Glyph, string Text)> DataNotes()
    {
        List<(string, string)> notes = [];
        if (_panel?.Providers.CurrentAIProvider(Purpose) is not { } provider) return notes;
        if (provider.LeavesPC) notes.Add(("", Loc.Format("Data sent to %@ leaves this PC.", provider.Name)));
        if (provider.CliPreset is { } preset && CLIRunner.ContainmentNote(preset) is { } note) notes.Add(("", Loc.T(note)));
        return notes;
    }

    private void BuildDataNotes()
    {
        DataNotesPanel.Children.Clear();
        foreach (var (glyph, text) in DataNotes())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon { Glyph = glyph, Style = (Style)Resources["NoteIconStyle"] });
            var block = new TextBlock { Text = text, Style = (Style)Resources["NoteTextStyle"] };
            Grid.SetColumn(block, 1);
            row.Children.Add(block);
            DataNotesPanel.Children.Add(row);
        }
    }

    // Copy

    /// <summary>
    /// Copy as prompt (macOS <c>copyPrompt</c>, Ctrl+Shift+C), on Summary only: the transcript as a summary prompt,
    /// without a question; kept out of the clipboard history and the cloud clipboard (macOS: transient, this Mac only).
    /// False off Summary or without a transcript.
    /// </summary>
    internal bool CopyPrompt()
    {
        if (_tab != DetailTab.Summary || _panel?.CopyPrompt(null) is not { } prompt) return false;
        CopyText(prompt, Loc.T("Copied — paste into any chat"), transient: true);
        return true;
    }

    private void CopyText(string text, string toast, bool transient = false)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            if (transient) Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
            else Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not copy: {e.GetType().Name}");
            return;
        }
        ShowToast(toast);
    }

    /// <summary>Shows <paramref name="text"/> for 2 s; a newer one replaces it and gets its own 2 s.</summary>
    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // Links

    private MarkdownLinks Links() => new(Seek: seconds => _seek?.Invoke(seconds), OpenUrl: url => _ = OpenUrlAsync(url));

    /// <summary>
    /// A web link from model output opens in the browser once the dialog showing its destination is confirmed (macOS
    /// <c>WebLinkConfirmation</c>); only http(s), checked again here.
    /// </summary>
    private async Task OpenUrlAsync(Uri url)
    {
        if (_model?.Confirm is not { } confirm || url.Scheme is not ("http" or "https")) return;
        var host = url.IdnHost is { Length: > 0 } name ? name : Loc.T("this link");
        if (!await confirm(Loc.Format("Open %@?", host), url.AbsoluteUri, Loc.T("Open"))) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open a link: {e.GetType().Name}");
        }
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
