using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using Span = Microsoft.UI.Xaml.Documents.Span;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The transcript of the conversation page, left of its AI panel (macOS TranscriptView.swift), for a
/// <see cref="TranscriptViewModel"/> and the page's <see cref="PlaybackController"/>: the segments as one selectable
/// text (macOS TranscriptTextView; a click on a start or a name plays from the segment; the one under the play head has
/// a fill, and the text follows it while playing until the user scrolls), the version menu, the transcription's state, the speaker notes, playback, the
/// speaker chips with their rename field, Show/Hide video, Copy, Search, Re-transcribe and Summarize.
/// </summary>
public sealed partial class TranscriptView : UserControl
{
    /// <summary>Up to three speaker chips; "+N" lists the others.</summary>
    private const int MaxSpeakerChips = 3;

    /// <summary>Segments added to the text per dispatcher turn, so a long transcript never blocks the UI thread for long.</summary>
    private const int BatchSize = 200;

    /// <summary>The start and the name: CaptionTextBlockStyle's size.</summary>
    private const double CaptionFontSize = 12;

    /// <summary>Between the start and the dot, and the dot before the name: <see cref="TranscriptDocument"/>'s "  ● ".</summary>
    private const string SpacerText = "  ";
    private const string DotText = "● ";

    /// <summary>Space kept above a segment the text scrolls to.</summary>
    private const double ScrollMargin = 8;

    /// <summary>How far the play head's fill reaches past its paragraph's text (macOS <c>fillPadding</c>).</summary>
    private static readonly Size FillPadding = new(10, 6);

    /// <summary>Between segments, about the old rows' spacing.</summary>
    private static readonly Thickness SegmentMargin = new(0, 0, 0, 14);

    private static readonly UISettings UISettings = new();

    /// <summary>The search's matches under the text (macOS <c>findHighlightColor</c>): lighter, the current one stronger.</summary>
    private static readonly SolidColorBrush MatchBrush = new(Color.FromArgb(0x66, 0xFF, 0xD6, 0x00));
    private static readonly SolidColorBrush CurrentMatchBrush = new(Color.FromArgb(0xCC, 0xFF, 0x9A, 0x00));

    private AppViewModel? _model;
    private TranscriptViewModel? _transcript;
    private PlaybackController? _playback;

    /// <summary>
    /// The shown version as one text (null without a transcript), its segments in the same order, and each segment's
    /// index by id. <see cref="_blocks"/> holds the paragraphs added so far, one per entry, in order.
    /// </summary>
    private TranscriptDocument? _document;
    private IReadOnlyList<Transcript.Segment> _segments = [];
    private Dictionary<Guid, int> _indexById = new();
    private readonly List<SegmentBlock> _blocks = [];

    /// <summary>Bumped at every rebuild and at <see cref="Detach"/>, so the batches of an older build stop.</summary>
    private int _buildGeneration;

    /// <summary>
    /// <see cref="Playable"/> when the text was last built: its starts and names are links only then, so the text is
    /// built again when it changes (the audio deleted, or found missing).
    /// </summary>
    private bool _playable;

    /// <summary>
    /// The text follows the play head: set by Play and by a click on a start or a name, cleared when the user scrolls
    /// or a citation shows (macOS TranscriptTextView <c>following</c>).
    /// </summary>
    private bool _following;

    /// <summary>The segment with the play head's fill, the one with the speaker icon, and whether playback was on at the last update.</summary>
    private int? _highlighted;
    private int? _speakingIndex;
    private bool _wasPlaying;
    private bool _marksPending;

    /// <summary>A citation's segment to scroll to once its paragraph is in the text.</summary>
    private Guid? _pendingSegment;

    /// <summary>
    /// Where the search finds the query (<see cref="SearchText.PhraseRanges"/>): per match its segment and its place in
    /// that segment's body, in order; the current one; the query they were found for (the same query in a changed text,
    /// a rename, keeps its place); bumped at every search, so an older one is dropped.
    /// </summary>
    private List<(int Segment, int Start, int Length)> _matches = [];
    private int _matchIndex;
    private string _searchedQuery = "";
    private int _searchGeneration;

    /// <summary>The text's context menu: Play from here, Copy, Select All.</summary>
    private readonly MenuFlyout _textMenu = new();

    /// <summary>The speaker whose rename field is open.</summary>
    private SpeakerChip? _renaming;

    /// <summary>What the chips were last built from: they are rebuilt only when it changes, not on every job update.</summary>
    private IReadOnlyList<SpeakerChip>? _chipSpeakers;
    private bool _chipsEnabled;
    /// <summary>The chips <see cref="ChipsPanel"/> has room for; the speakers after them are behind "+N".</summary>
    private int _chipsShown;

    private string? _retranscribeTip;
    private string? _transcribingTip;
    private string? _summarizeTip;
    private string? _audioRemovedTip;
    private string? _videoToggleTip;
    private string? _deleteVideoTip;

    /// <summary>What <see cref="FitControlRow"/> last fitted: the width, the chips and the buttons' state.</summary>
    private object? _rowFitted;
    private string? _stepTip;
    private string? _failureTip;
    private string? _retryTip;
    private string? _transcribeTip;

    /// <summary>One segment's paragraph and the inlines that change: names on a rename, colors on a theme change.</summary>
    private sealed record SegmentBlock(
        Paragraph Paragraph, Span TimeLink, Run Time, Run Spacer, Span NameLink, Run Dot, Run Name, Run Body,
        int ColorIndex);

    public TranscriptView()
    {
        InitializeComponent();
        ChipsPanel.ShownChanged += OnChipsShown;
        ControlRow.SizeChanged += (_, _) => FitControlRow();
        FollowUserScrolling();
        SegmentText.PreviewKeyDown += OnTextPreviewKeyDown;
        // Also when the text already handled it: RichTextBlock's own menu (Copy, Select All, removed in the XAML) may
        // take the request before handlers added with +=.
        SegmentText.AddHandler(ContextRequestedEvent,
            new TypedEventHandler<UIElement, ContextRequestedEventArgs>(OnTextContextRequested), handledEventsToo: true);
        // A new width wraps the text again: the fill and the speaker icon move with their paragraph. The first layout
        // (the tab or the text has just shown) lets a waiting citation scroll.
        SegmentText.SizeChanged += (_, _) =>
        {
            PlaceMarksLater();
            if (_pendingSegment is not null) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowPendingSegment);
        };
        // After the theme walk, once the swatches hold the new theme's brushes.
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyColors);
        VersionMenu.Opening += (_, _) => BuildVersionMenu();
        RetranscribeMenu.Opening += (_, _) => BuildRetranscribeMenu();
        RetryMenu.Opening += (_, _) => BuildRetryMenu();
        RetryButton.Click += (_, _) => Transcribe(null);
        TranscribeButton.Click += (_, _) => Transcribe(null);
        CopyButton.Click += (_, _) => Copy();
        SearchButton.Click += (_, _) => SetSearch(SearchBar.Visibility != Visibility.Visible);
        SearchBox.TextChanged += (_, _) => _ = UpdateSearchAsync();
        SearchBox.QuerySubmitted += (_, _) => StepSearch(1);
        SearchBox.PreviewKeyDown += OnSearchKeyDown;
        PreviousMatchButton.Click += (_, _) => StepSearch(-1);
        NextMatchButton.Click += (_, _) => StepSearch(1);
        CloseSearchButton.Click += (_, _) => SetSearch(false);
        SegmentScroller.ViewChanged += (_, _) => PlaceMarksLater();
        VideoToggleButton.Click += (_, _) => ToggleVideo();
        ExportVideoButton.Click += (_, _) => _ = ExportVideoAsync();
        DeleteVideoButton.Click += (_, _) => _ = DeleteVideoAsync();
        StopTranscriptionButton.Click += (_, _) =>
        {
            if (_transcript is not null) _model?.StopTranscription(_transcript.ConversationId);
        };
        SummarizeButton.Click += (_, _) => Summarize();
        NeedsModelBadge.Click += (_, _) => _model?.OpenLocalAI(ProvidersSettingsPage.TranscriptionGroup);
        NoticeDownloadButton.Click += (_, _) => _model?.OpenLocalAI(ProvidersSettingsPage.DiarizationGroup);
        RenameBox.KeyDown += OnRenameKeyDown;
        RenameFlyout.Opened += (_, _) =>
        {
            RenameBox.Focus(FocusState.Programmatic);
            RenameBox.SelectAll();
        };
        RenameFlyout.Closed += (_, _) => _renaming = null;
    }

    /// <summary>Shows a dialog through the window's dialog queue (Export video…, Delete video…); false when another is open.</summary>
    internal Func<ContentDialog, Task<bool>>? ShowDialog { get; set; }

    /// <summary>Shows <paramref name="transcript"/> and plays with <paramref name="playback"/>; call when the page shows.</summary>
    public void Attach(AppViewModel model, TranscriptViewModel transcript, PlaybackController playback)
    {
        Detach();
        _model = model;
        _transcript = transcript;
        _playback = playback;
        model.PropertyChanged += OnModelChanged;
        transcript.PropertyChanged += OnTranscriptChanged;
        playback.PropertyChanged += OnPlaybackChanged;
        Player.Attach(model, playback);
        SetSearch(false);
        UpdateLines();
        _chipSpeakers = null;
        UpdateChips();
        Update();
    }

    /// <summary>
    /// A clicked AI citation (macOS <c>scrollTarget</c>): scrolls the segment <paramref name="id"/> to the middle once
    /// its paragraph is in the text and laid out (its tab has just shown), and stops following the play head.
    /// </summary>
    internal void ShowSegment(Guid id) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (_model is not { IsClosing: false }) return;
        _following = false;
        _pendingSegment = id;
        ShowPendingSegment();
    });

    /// <summary>Stops following the models; call when the page goes.</summary>
    public void Detach()
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        if (_transcript is not null) _transcript.PropertyChanged -= OnTranscriptChanged;
        if (_playback is not null) _playback.PropertyChanged -= OnPlaybackChanged;
        Player.Detach();
        RenameFlyout.Hide();
        MoreSpeakersFlyout.Hide();
        _textMenu.Hide();
        // A build still adding paragraphs stops; the next Attach builds the text again.
        _buildGeneration++;
        _model = null;
        _transcript = null;
        _playback = null;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
            case nameof(AppViewModel.InstalledModelIds):
                Update();
                break;
            case nameof(AppViewModel.AIJobs):
            case nameof(AppViewModel.Conversations):
                UpdateSummarize();
                break;
            case nameof(AppViewModel.Settings):
                UpdateVideoToggle();
                break;
        }
    }

    private void OnTranscriptChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TranscriptViewModel.Lines):
                UpdateLines();
                break;
            case nameof(TranscriptViewModel.Speakers):
            case nameof(TranscriptViewModel.CanRename):
                UpdateChips();
                FitControlRow();
                break;
            default:
                Update();
                break;
        }
    }

    /// <summary>Not once the window closed: its controls fail every call.</summary>
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not { IsClosing: false }) return;
        switch (e.PropertyName)
        {
            case nameof(PlaybackController.ActiveSegmentId):
            case nameof(PlaybackController.IsPlaying):
                UpdateActive();
                break;
        }
    }

    // Segments

    /// <summary>The shown version has segments: Copy and Summarize show for it, the card shows the text (or a ring while it is built).</summary>
    private bool HasSegments => _document is { Entries.Count: > 0 };

    /// <summary>
    /// Segment clicks and the transport play the audio, or the video without it (macOS <c>mediaPlayable</c>): audio not
    /// deleted, not being recorded or compressed, or a video not being recorded (macOS TranscriptView <c>load</c>).
    /// </summary>
    private bool Playable => _transcript?.Conversation is
        { HasAudio: true, Status: not (ConversationStatus.Recording or ConversationStatus.Compressing) } || HasVideo;

    /// <summary>The conversation has a video to show and play: not while it is being recorded (macOS <c>videoPlayable</c>).</summary>
    private bool HasVideo => _transcript?.Conversation is { HasVideo: true, Status: not ConversationStatus.Recording };

    /// <summary>
    /// The shown version's text (macOS TranscriptTextView <c>apply</c>). The same segments with other names (a speaker
    /// renamed) change the names in place, so the text keeps its scroll position; other segments (another version, a
    /// new transcript) build the text again from the top.
    /// </summary>
    private void UpdateLines()
    {
        if (_transcript is null) return;
        var shown = _transcript.ShownTranscript;
        IReadOnlyList<Transcript.Segment> segments = shown?.Segments ?? [];
        var document = shown is { } transcript ? new TranscriptDocument(segments, s => transcript.DisplayName(s.Speaker)) : null;
        if (document is not null && _document is { } old && _blocks.Count == old.Entries.Count && _playable == Playable
            && OnlyNamesDiffer(old, _segments, document, segments))
        {
            Rename(old, document, segments);
        }
        else
        {
            Rebuild(document, segments);
        }
        CopyButton.IsEnabled = HasSegments;
        SearchButton.IsEnabled = HasSegments || SearchBar.Visibility == Visibility.Visible;
        _ = UpdateSearchAsync();
        UpdateSummarize();
        UpdateContent();
        UpdateActive();
    }

    /// <summary>Same segments (ids, starts, speakers and texts), all of them in the text: only names may differ.</summary>
    private static bool OnlyNamesDiffer(
        TranscriptDocument old, IReadOnlyList<Transcript.Segment> oldSegments,
        TranscriptDocument document, IReadOnlyList<Transcript.Segment> segments)
    {
        if (old.Entries.Count != document.Entries.Count || oldSegments.Count != segments.Count) return false;
        for (var i = 0; i < segments.Count; i++)
        {
            TranscriptDocument.Entry before = old.Entries[i], after = document.Entries[i];
            if (before.Id != after.Id || before.Time != after.Time || oldSegments[i].Speaker != segments[i].Speaker
                || !Body(old, before).SequenceEqual(Body(document, after))) return false;
        }
        return true;
    }

    private static ReadOnlySpan<char> Body(TranscriptDocument document, TranscriptDocument.Entry entry) =>
        document.Text.AsSpan(entry.BodyRange.Start, entry.BodyRange.Length);

    /// <summary>The renamed speakers' names in place; the fill and the speaker icon move with the text.</summary>
    private void Rename(TranscriptDocument old, TranscriptDocument document, IReadOnlyList<Transcript.Segment> segments)
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            if (old.Entries[i].Name != document.Entries[i].Name) _blocks[i].Name.Text = document.Entries[i].Name;
        }
        _document = document;
        _segments = segments;
        PlaceMarksLater();
    }

    /// <summary>
    /// Empties the text and adds <paramref name="document"/>'s paragraphs in batches at low priority (the ring shows
    /// until the first is in), scrolled to the top.
    /// </summary>
    private void Rebuild(TranscriptDocument? document, IReadOnlyList<Transcript.Segment> segments)
    {
        var generation = ++_buildGeneration;
        SetHighlight(null);
        _speakingIndex = null;
        SpeakingIcon.Visibility = Visibility.Collapsed;
        SegmentText.Blocks.Clear();
        _blocks.Clear();
        _playable = Playable;
        _document = document;
        _segments = segments;
        _indexById = new();
        foreach (var entry in document?.Entries ?? []) _indexById.TryAdd(entry.Id, entry.Index);
        SegmentScroller.ChangeView(null, 0, null, true);
        if (HasSegments) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => AddBatch(generation, 0));
    }

    /// <summary>The next <see cref="BatchSize"/> paragraphs from <paramref name="from"/>, then the next batch on a later turn.</summary>
    private void AddBatch(int generation, int from)
    {
        if (generation != _buildGeneration || _model is not { IsClosing: false } || _document is not { } document) return;
        var to = Math.Min(from + BatchSize, document.Entries.Count);
        var tip = Loc.T("Play from here");
        for (var i = from; i < to; i++)
        {
            var block = NewBlock(document, i, tip);
            _blocks.Add(block);
            SegmentText.Blocks.Add(block.Paragraph);
        }
        if (from == 0) UpdateContent();
        // The play head's segment and a citation's may be in this batch.
        UpdateActive();
        ShowPendingSegment();
        if (to < document.Entries.Count) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => AddBatch(generation, to));
    }

    /// <summary>
    /// The segment's paragraph: its start (secondary, tabular digits) and its dot and name (semibold, in the speaker's
    /// color), both links that play from the segment's start (<paramref name="tip"/> on the start) when there is audio,
    /// then the text on the next line.
    /// </summary>
    private SegmentBlock NewBlock(TranscriptDocument document, int index, string tip)
    {
        var entry = document.Entries[index];
        var colorIndex = TranscriptViewModel.ColorIndex(_segments[index].Speaker);
        var time = new Run { Text = entry.Time };
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        var timeLink = PlayLink(index, SecondarySwatch.Foreground);
        timeLink.Inlines.Add(time);
        if (_playable) ToolTipService.SetToolTip(timeLink, tip);
        var spacer = new Run { Text = SpacerText, FontSize = CaptionFontSize };
        var dot = new Run { Text = DotText };
        var name = new Run { Text = entry.Name };
        var nameLink = PlayLink(index, SpeakerBrush(colorIndex));
        nameLink.FontWeight = FontWeights.SemiBold;
        nameLink.Inlines.Add(dot);
        nameLink.Inlines.Add(name);
        var body = new Run { Text = Body(document, entry).ToString() };
        var paragraph = new Paragraph { Margin = SegmentMargin };
        paragraph.Inlines.Add(timeLink);
        paragraph.Inlines.Add(spacer);
        paragraph.Inlines.Add(nameLink);
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(body);
        return new SegmentBlock(paragraph, timeLink, time, spacer, nameLink, dot, name, body, colorIndex);
    }

    /// <summary>
    /// A caption-size link without underline that plays from segment <paramref name="index"/>; not a tab stop, so Tab
    /// leaves the text at once instead of visiting two links per segment. Plain text without audio to play.
    /// </summary>
    private Span PlayLink(int index, Brush foreground)
    {
        if (!_playable) return new Span { Foreground = foreground, FontSize = CaptionFontSize };
        var link = new Hyperlink
        {
            Foreground = foreground,
            FontSize = CaptionFontSize,
            UnderlineStyle = UnderlineStyle.None,
            IsTabStop = false,
        };
        link.Click += (_, _) => Play(index);
        return link;
    }

    /// <summary>Plays from the segment's start and follows it (the old row click); nothing without audio.</summary>
    private void Play(int index)
    {
        if (index >= _segments.Count || _playback is not { IsReady: true } playback) return;
        _following = true;
        playback.SeekTo(_segments[index]);
    }

    /// <summary>
    /// The fill on the segment under the play head, the speaker icon after its name while it plays; the text scrolls to
    /// it while following, only once it is out of view. Play starts following (macOS TranscriptTextView).
    /// </summary>
    private void UpdateActive()
    {
        if (_playback is not { } playback) return;
        int? active = playback.ActiveSegmentId is { } id && _indexById.TryGetValue(id, out var index) && index < _blocks.Count
            ? index
            : null;
        if (active != _highlighted) SetHighlight(active);

        var speaking = playback.IsPlaying ? active : null;
        if (speaking != _speakingIndex)
        {
            _speakingIndex = speaking;
            PlaceMarksLater();
        }

        if (playback.IsPlaying && !_wasPlaying) _following = true;
        _wasPlaying = playback.IsPlaying;
        if (_following && playback.IsPlaying && active is { } line) ScrollIntoViewIfOutside(line);
    }

    /// <summary>
    /// The play head's fill on paragraph <paramref name="index"/> (none for null), placed once the text is laid out;
    /// none goes at once, so a rebuilt text never shows the old one.
    /// </summary>
    private void SetHighlight(int? index)
    {
        _highlighted = index;
        if (index is null) ActiveFill.Visibility = Visibility.Collapsed;
        PlaceMarksLater();
    }

    /// <summary>The fill and the speaker icon on their paragraphs once the text is laid out, once per turn.</summary>
    private void PlaceMarksLater()
    {
        if (_marksPending) return;
        _marksPending = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _marksPending = false;
            if (_model is not { IsClosing: false }) return;
            PlaceFill();
            PlaceSpeakingIcon();
            PlaceSearchMarks();
        });
    }

    /// <summary>
    /// The play head's fill behind paragraph <see cref="_highlighted"/> (macOS SegmentFragment's fill): the text's
    /// width and its lines' height, plus <see cref="FillPadding"/> on each side; hidden without one or a layout. A
    /// rounded border on a canvas under the text, placed from the paragraph's character rects like the speaker icon.
    /// Not a TextHighlighter: its ranges count the text's characters, not the <see cref="TextPointer.Offset"/>s a
    /// paragraph gives (those also count element edges, several per paragraph), and it recolors the text.
    /// </summary>
    private void PlaceFill()
    {
        var paragraph = _highlighted is { } index && index < _blocks.Count ? _blocks[index].Paragraph : null;
        var top = paragraph?.ContentStart.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
        var bottom = paragraph?.ContentEnd.GetCharacterRect(LogicalDirection.Backward) ?? Rect.Empty;
        if (top.IsEmpty || bottom.IsEmpty)
        {
            ActiveFill.Visibility = Visibility.Collapsed;
            return;
        }
        // The canvas lies under the text, at its origin.
        Canvas.SetLeft(ActiveFill, -FillPadding.Width);
        Canvas.SetTop(ActiveFill, top.Top - FillPadding.Height);
        ActiveFill.Width = SegmentText.ActualWidth + 2 * FillPadding.Width;
        ActiveFill.Height = Math.Max(0, bottom.Bottom - top.Top) + 2 * FillPadding.Height;
        ActiveFill.Visibility = Visibility.Visible;
    }

    /// <summary>The speaker icon in the speaker's color, after the name of <see cref="_speakingIndex"/>; hidden without one.</summary>
    private void PlaceSpeakingIcon()
    {
        var block = _speakingIndex is { } index && index < _blocks.Count ? _blocks[index] : null;
        var rect = block?.Name.ContentEnd.GetCharacterRect(LogicalDirection.Backward) ?? Rect.Empty;
        if (block is null || rect.IsEmpty)
        {
            SpeakingIcon.Visibility = Visibility.Collapsed;
            return;
        }
        // The canvas lies over the text, at its origin.
        Canvas.SetLeft(SpeakingIcon, rect.Right + 6);
        Canvas.SetTop(SpeakingIcon, rect.Top + (rect.Height - SpeakingIcon.FontSize) / 2);
        SpeakingIcon.Foreground = SpeakerBrush(block.ColorIndex);
        SpeakingIcon.Visibility = Visibility.Visible;
    }

    /// <summary>The segment's top and bottom in the viewport, once the text is laid out; null without a layout.</summary>
    private (double Top, double Bottom)? SegmentBounds(int index)
    {
        SegmentScroller.UpdateLayout();
        var paragraph = _blocks[index].Paragraph;
        var top = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var bottom = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        if (top.IsEmpty || bottom.IsEmpty) return null;
        var transform = SegmentText.TransformToVisual(SegmentScroller);
        return (transform.TransformPoint(new Point(0, top.Top)).Y, transform.TransformPoint(new Point(0, bottom.Bottom)).Y);
    }

    /// <summary>
    /// Scrolls the segment to the top of the view when it is not fully in view (the old list's Leading), without
    /// animation like the old ScrollIntoView: an animated ChangeView may raise DirectManipulationStarted, which would
    /// stop following.
    /// </summary>
    private void ScrollIntoViewIfOutside(int index)
    {
        if (SegmentBounds(index) is not { } bounds) return;
        if (bounds.Top >= 0 && bounds.Bottom <= SegmentScroller.ViewportHeight) return;
        SegmentScroller.ChangeView(null, Math.Max(0, SegmentScroller.VerticalOffset + bounds.Top - ScrollMargin), null, true);
    }

    /// <summary>
    /// A citation's segment in the middle of the view (its top when taller than the view), once it is in the text and
    /// laid out; dropped when the shown version does not have it.
    /// </summary>
    private void ShowPendingSegment()
    {
        if (_pendingSegment is not { } id || _model is not { IsClosing: false }) return;
        if (!_indexById.TryGetValue(id, out var index))
        {
            _pendingSegment = null;
            return;
        }
        if (index >= _blocks.Count || SegmentBounds(index) is not { } bounds) return;
        _pendingSegment = null;
        var height = bounds.Bottom - bounds.Top;
        var viewport = SegmentScroller.ViewportHeight;
        var above = height > viewport ? ScrollMargin : (viewport - height) / 2;
        SegmentScroller.ChangeView(null, Math.Max(0, SegmentScroller.VerticalOffset + bounds.Top - above), null, true);
    }

    /// <summary>
    /// The user's own scrolling (wheel, touch, scroll bar, keys) stops following the play head; a press in the text
    /// (a selection, a link) does not.
    /// </summary>
    private void FollowUserScrolling()
    {
        SegmentScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => _following = false), handledEventsToo: true);
        SegmentScroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, SegmentText)) _following = false;
        }), handledEventsToo: true);
        SegmentScroller.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);
        SegmentScroller.DirectManipulationStarted += (_, _) => _following = false;
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            _following = false;
        }
    }

    // Search

    /// <summary>
    /// Shows the search field, sliding up from the line above the bar, focused, or hides it and clears the query (and so
    /// the matches) (macOS <c>setSearch</c>).
    /// </summary>
    private void SetSearch(bool shown)
    {
        if (!shown)
        {
            SearchBar.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";
            UpdateSearchButton();
            _ = UpdateSearchAsync();
            return;
        }
        SearchBar.TranslationTransition = null;
        SearchBar.OpacityTransition = null;
        if (UISettings.AnimationsEnabled)
        {
            SearchBar.Translation = new Vector3(0, 12, 0);
            SearchBar.Opacity = 0;
        }
        SearchBar.Visibility = Visibility.Visible;
        UpdateSearchButton();
        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBar.TranslationTransition = new Vector3Transition();
            SearchBar.OpacityTransition = new ScalarTransition();
            SearchBar.Translation = Vector3.Zero;
            SearchBar.Opacity = 1;
            SearchBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>The search button's icon in the accent color while the field shows.</summary>
    private void UpdateSearchButton()
    {
        if (SearchBar.Visibility == Visibility.Visible) SearchIcon.Foreground = SpeakerBrush(-1);
        else SearchIcon.ClearValue(IconElement.ForegroundProperty);
        SearchButton.IsEnabled = HasSegments || SearchBar.Visibility == Visibility.Visible;
    }

    /// <summary>Enter goes to the next match, Shift+Enter to the previous one; Esc clears the query, then hides the field.</summary>
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                StepSearch(ConversationListView.IsKeyDown(VirtualKey.Shift) ? -1 : 1);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else SetSearch(false);
                break;
        }
    }

    /// <summary>The next (1) or previous (-1) match, round from the last to the first.</summary>
    private void StepSearch(int step)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + step + _matches.Count) % _matches.Count;
        ShowCurrentMatch();
    }

    /// <summary>Finds the query in the shown text off the UI thread; a new query starts at its first match.</summary>
    private async Task UpdateSearchAsync()
    {
        var generation = ++_searchGeneration;
        var query = SearchBar.Visibility == Visibility.Visible ? SearchBox.Text : "";
        List<(int Segment, int Start, int Length)> matches = [];
        if (_document is { } document && !string.IsNullOrWhiteSpace(query))
        {
            // Typos only when nothing is found without them (macOS searchMatches).
            matches = await Task.Run(() =>
            {
                List<(int Segment, int Start, int Length)> Found(bool typos)
                {
                    List<(int Segment, int Start, int Length)> found = [];
                    foreach (var entry in document.Entries)
                    {
                        foreach (var (start, length) in SearchText.PhraseRanges(Body(document, entry).ToString(), query, typos))
                            found.Add((entry.Index, start, length));
                    }
                    return found;
                }
                var exact = Found(typos: false);
                return exact.Count > 0 ? exact : Found(typos: true);
            });
            if (generation != _searchGeneration || _model is not { IsClosing: false }) return;
        }
        _matchIndex = query == _searchedQuery ? Math.Clamp(_matchIndex, 0, Math.Max(matches.Count - 1, 0)) : 0;
        _matches = matches;
        _searchedQuery = query;
        ShowCurrentMatch();
    }

    /// <summary>"n/m" or No results beside the field, the marks, and the current match scrolled into view.</summary>
    private void ShowCurrentMatch()
    {
        SearchCountText.Text = string.IsNullOrWhiteSpace(_searchedQuery) ? ""
            : _matches.Count == 0 ? Loc.T("No results") : $"{_matchIndex + 1}/{_matches.Count}";
        PreviousMatchButton.IsEnabled = NextMatchButton.IsEnabled = _matches.Count > 0;
        PlaceMarksLater();
        if (_matches.Count == 0 || _matches[_matchIndex].Segment >= _blocks.Count) return;
        _following = false;
        var match = _matches[_matchIndex];
        SegmentScroller.UpdateLayout();
        if (MatchRects(match) is not { Count: > 0 } rects) return;
        var transform = SegmentText.TransformToVisual(SegmentScroller);
        var top = transform.TransformPoint(new Point(0, rects[0].Top)).Y;
        var bottom = transform.TransformPoint(new Point(0, rects[^1].Bottom)).Y;
        var viewport = SegmentScroller.ViewportHeight;
        if (top >= 0 && bottom <= viewport) return;
        SegmentScroller.ChangeView(null, Math.Max(0, SegmentScroller.VerticalOffset + top - (viewport - (bottom - top)) / 2), null, true);
    }

    /// <summary>
    /// The marks of the matches in the paragraphs in view (a long transcript has too many for all), the current one
    /// stronger: rounded borders on a canvas under the text, from the body runs' character rects like the fill.
    /// </summary>
    private void PlaceSearchMarks()
    {
        SearchMarks.Children.Clear();
        if (_matches.Count == 0 || _blocks.Count == 0) return;
        var offset = -SegmentText.TransformToVisual(SegmentScroller).TransformPoint(new Point(0, 0)).Y;
        var first = SegmentAt(offset) ?? 0;
        var last = SegmentAt(offset + SegmentScroller.ViewportHeight) ?? _blocks.Count - 1;
        for (var i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];
            if (match.Segment < first || match.Segment > last || match.Segment >= _blocks.Count) continue;
            foreach (var rect in MatchRects(match) ?? [])
            {
                var mark = new Border
                {
                    Width = Math.Max(0, rect.Width),
                    Height = Math.Max(0, rect.Height),
                    CornerRadius = new CornerRadius(3),
                    Background = i == _matchIndex ? CurrentMatchBrush : MatchBrush,
                };
                Canvas.SetLeft(mark, rect.Left);
                Canvas.SetTop(mark, rect.Top);
                SearchMarks.Children.Add(mark);
            }
        }
    }

    /// <summary>
    /// The match's rects in the text's coordinates, one per line it is on (from its first character's left to the text's
    /// right edge, full lines, then to its last character's right); null without a layout.
    /// </summary>
    private List<Rect>? MatchRects((int Segment, int Start, int Length) match)
    {
        var body = _blocks[match.Segment].Body;
        if (match.Start + match.Length > body.Text.Length) return null;
        var from = body.ContentStart.GetPositionAtOffset(match.Start, LogicalDirection.Forward);
        var to = body.ContentStart.GetPositionAtOffset(match.Start + match.Length, LogicalDirection.Backward);
        if (from is null || to is null) return null;
        var start = from.GetCharacterRect(LogicalDirection.Forward);
        var end = to.GetCharacterRect(LogicalDirection.Backward);
        if (start.IsEmpty || end.IsEmpty) return null;
        if (end.Top < start.Bottom - 1) return [new Rect(start.Left, start.Top, end.Right - start.Left, Math.Max(start.Height, end.Height))];
        var width = SegmentText.ActualWidth;
        List<Rect> rects = [new Rect(start.Left, start.Top, width - start.Left, start.Height)];
        if (end.Top > start.Bottom + 1) rects.Add(new Rect(0, start.Bottom, width, end.Top - start.Bottom));
        rects.Add(new Rect(0, end.Top, end.Right, end.Height));
        return rects;
    }

    // Selection

    /// <summary>
    /// Ctrl+C (and Ctrl+Insert) with a selection copies it as "Name [mm:ss]: text" lines, before the text's own copy of
    /// the plain text. Not with Shift or Alt: Ctrl+Shift+C is the window's Copy as prompt.
    /// </summary>
    private void OnTextPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.C or VirtualKey.Insert) || !ConversationListView.IsKeyDown(VirtualKey.Control)
            || ConversationListView.IsKeyDown(VirtualKey.Shift) || ConversationListView.IsKeyDown(VirtualKey.Menu)) return;
        if (SelectionCopyText() is not { } text) return;
        e.Handled = true;
        // After the key's own handling, so a copy the text still makes cannot replace this one.
        DispatcherQueue.TryEnqueue(() => SetClipboard(text));
    }

    /// <summary>
    /// The text's menu (macOS: the standard menu with Play from here first): Play from here for the segment under the
    /// pointer (under the selection's start from the keyboard), Copy and Select All.
    /// </summary>
    private void OnTextContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        var hasPoint = args.TryGetPosition(SegmentText, out var point);
        var segment = hasPoint
            ? SegmentAt(point.Y)
            : SegmentText.SelectionStart is { } start && _document is { } document
                ? document.SegmentAt(PlainOffset(start))?.Index
                : null;
        _textMenu.Items.Clear();
        if (segment is { } index && _playable)
        {
            var play = new MenuFlyoutItem { Text = Loc.T("Play from here"), IsEnabled = _playback is { IsReady: true } };
            play.Click += (_, _) => Play(index);
            _textMenu.Items.Add(play);
            _textMenu.Items.Add(new MenuFlyoutSeparator());
        }
        var copy = new MenuFlyoutItem { Text = Loc.T("Copy"), IsEnabled = SegmentText.SelectedText.Length > 0 };
        copy.Click += (_, _) =>
        {
            if (SelectionCopyText() is { } text) SetClipboard(text);
        };
        _textMenu.Items.Add(copy);
        var selectAll = new MenuFlyoutItem { Text = Loc.T("Select All") };
        selectAll.Click += (_, _) => SegmentText.SelectAll();
        _textMenu.Items.Add(selectAll);
        _textMenu.ShowAt(SegmentText, hasPoint ? new FlyoutShowOptions { Position = point } : new FlyoutShowOptions());
    }

    /// <summary>
    /// The paragraph at <paramref name="y"/> in the text's coordinates: the last one whose first line starts at or
    /// above it (the space under a paragraph is its own, above the first is the first's); null without a layout. From
    /// the paragraphs' character rects, like the fill.
    /// </summary>
    private int? SegmentAt(double y)
    {
        if (_blocks.Count == 0) return null;
        int low = 0, high = _blocks.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            var top = _blocks[mid].Paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (top.IsEmpty) return null;
            if (top.Top <= y) low = mid + 1;
            else high = mid;
        }
        return Math.Max(0, low - 1);
    }

    /// <summary>The selection as <see cref="TranscriptDocument.CopyText"/> gives it; null without a selection.</summary>
    private string? SelectionCopyText()
    {
        if (_document is not { } document || _blocks.Count == 0 || SegmentText.SelectedText.Length == 0) return null;
        if (SegmentText.SelectionStart is not { } from || SegmentText.SelectionEnd is not { } to) return null;
        int start = PlainOffset(from), end = PlainOffset(to);
        if (end < start) (start, end) = (end, start);
        return end > start ? document.CopyText(new TranscriptDocument.TextRange(start, end - start)) : null;
    }

    /// <summary>
    /// <paramref name="pointer"/> as an index into <see cref="_document"/>'s text. A pointer's offset also counts
    /// element edges, so it is read relative to the run it is in (each run is a known piece of the document); a pointer
    /// between runs (an element edge, the line break) counts as the end of the run before it.
    /// </summary>
    private int PlainOffset(TextPointer pointer)
    {
        if (_document is not { } document || _blocks.Count == 0) return 0;
        var offset = pointer.Offset;
        // The last paragraph starting at or before the pointer.
        int low = 0, high = _blocks.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_blocks[mid].Paragraph.ContentStart.Offset <= offset) low = mid + 1;
            else high = mid;
        }
        if (low == 0) return 0;
        var block = _blocks[low - 1];
        var entry = document.Entries[low - 1];
        // Last run first: the first one starting at or before the pointer holds it.
        (Run Run, int Start)[] runs =
        [
            (block.Body, entry.BodyRange.Start),
            (block.Name, entry.NameRange.Start),
            (block.Dot, entry.NameRange.Start - DotText.Length),
            (block.Spacer, entry.TimeRange.End),
            (block.Time, entry.TimeRange.Start),
        ];
        foreach (var (run, start) in runs)
        {
            var runStart = run.ContentStart.Offset;
            if (offset >= runStart) return start + Math.Clamp(offset - runStart, 0, run.Text.Length);
        }
        return entry.HeaderRange.Start;
    }

    // Colors

    /// <summary>
    /// The speaker's color (-1 is me, see <see cref="TranscriptViewModel.ColorIndex"/>) from the SpeakerMe and
    /// Speaker0…7 swatches, in the current theme.
    /// </summary>
    private Brush SpeakerBrush(int colorIndex) => ((TextBlock)Swatches.Children[colorIndex + 1]).Foreground;

    /// <summary>
    /// Light, Dark or contrast changed: the text's brushes from the swatches again (XAML styles and the fill follow by
    /// themselves).
    /// </summary>
    private void ApplyColors()
    {
        if (_model?.IsClosing == true) return;
        var secondary = SecondarySwatch.Foreground;
        foreach (var block in _blocks)
        {
            block.TimeLink.Foreground = secondary;
            block.NameLink.Foreground = SpeakerBrush(block.ColorIndex);
        }
        UpdateSearchButton();
        PlaceMarksLater();
    }

    /// <summary>The speaker's color as a caption style (SpeakerMe, Speaker0…7 in the resources).</summary>
    private Style SpeakerStyle(int colorIndex) => (Style)Resources[colorIndex < 0 ? "SpeakerMe" : $"Speaker{colorIndex}"];

    // State

    /// <summary>Everything but the lines and chips: the version menu, the empty note, the status, notes and actions.</summary>
    private void Update()
    {
        if (_transcript is null) return;
        if (_document is { } document && _playable != Playable) Rebuild(document, _segments);
        VersionButton.Visibility = Shown(_transcript.ShowsVersionMenu);
        VersionText.Text = _transcript.VersionLabel;
        AutomationProperties.SetName(VersionButton, _transcript.VersionLabel);
        // Older versions are view-only: a rename edits the newest.
        if (!_transcript.CanRename)
        {
            RenameFlyout.Hide();
            MoreSpeakersFlyout.Hide();
        }
        UpdateContent();
        UpdateStatus();
        UpdateNotice();
        UpdateActions();
    }

    /// <summary>The text once its first paragraphs are in, a ring while reading or building it, or why there are none.</summary>
    private void UpdateContent()
    {
        if (_transcript is null) return;
        var hasLines = _blocks.Count > 0;
        var loading = !hasLines && (HasSegments || _transcript.IsLoading);
        SegmentScroller.Visibility = Shown(hasLines);
        LoadingRing.Visibility = Shown(loading);
        LoadingRing.IsActive = loading;
        EmptyPanel.Visibility = Shown(!hasLines && !loading);
        EmptyText.Text = _transcript.EmptyText;
    }

    /// <summary>
    /// macOS TranscriptionStatus and TranscriptionFailureReason: in the empty card without a transcript; in the bar
    /// with one, where a running job shows on Re-transcribe instead.
    /// </summary>
    private void UpdateStatus()
    {
        if (_model is null || _transcript?.Conversation is not { } c)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            BarStatusHost.Visibility = Visibility.Collapsed;
            return;
        }
        _model.TranscriptionProgress.TryGetValue(c.Id, out var progress);
        var running = progress is not null && progress.Step != JobStep.NeedsModel;
        var failed = progress is null && c.Status == ConversationStatus.TranscriptionFailed;
        var transcribe = progress is null && !failed && !c.HasTranscript
            && c.Status is ConversationStatus.Ready or ConversationStatus.Transcribing;
        var showsProgress = running && !c.HasTranscript;

        ProgressPanel.Visibility = Shown(showsProgress);
        NeedsModelBadge.Visibility = Shown(progress is not null && !running);
        FailureText.Visibility = Shown(failed);
        RetryButton.Visibility = Shown(failed);
        TranscribeButton.Visibility = Shown(transcribe);
        StatusPanel.Visibility = Shown(showsProgress || (progress is not null && !running) || failed || transcribe);

        if (showsProgress)
        {
            var queued = progress!.Step is JobStep.Queued or JobStep.Deferred;
            JobRing.IsIndeterminate = queued;
            JobRing.Value = queued ? 0 : progress.Fraction;
            StepText.Text = _model.IsStopping(c.Id) ? Loc.T("Stopping…") : ConversationCard.StepTitle(progress.Step);
            SetTip(ProgressPanel, progress.Fraction.ToString("P0", CultureInfo.CurrentCulture), ref _stepTip);
        }
        if (failed)
        {
            var reason = _model.TranscriptionErrors.GetValueOrDefault(c.Id) ?? Loc.T("Transcription failed");
            FailureText.Text = reason;
            SetTip(FailureText, reason, ref _failureTip);
            RetryButton.IsEnabled = c.HasAudio;
            SetTip(RetryButton, Loc.T(c.HasAudio ? "Transcription failed — transcribe this conversation again" : "Audio has been removed"), ref _retryTip);
        }
        if (transcribe)
        {
            TranscribeButton.IsEnabled = c.HasAudio;
            SetTip(TranscribeButton, Loc.T(c.HasAudio ? "Transcribe this conversation" : "Audio has been removed"), ref _transcribeTip);
        }

        // Stacked under the empty note, in a row in the bar.
        var host = c.HasTranscript ? BarStatusHost : EmptyStatusHost;
        if (host.Child != StatusPanel)
        {
            BarStatusHost.Child = null;
            EmptyStatusHost.Child = null;
            host.Child = StatusPanel;
        }
        StatusPanel.Orientation = c.HasTranscript ? Orientation.Horizontal : Orientation.Vertical;
        StatusPanel.HorizontalAlignment = c.HasTranscript ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        FailureText.TextAlignment = c.HasTranscript ? TextAlignment.Left : TextAlignment.Center;
        BarStatusHost.Visibility = Shown(c.HasTranscript && StatusPanel.Visibility == Visibility.Visible);
    }

    /// <summary>The shown transcript was made without telling the speakers apart (macOS diarization notices).</summary>
    private void UpdateNotice()
    {
        var notes = _transcript?.ShownTranscript?.Notes ?? [];
        var localMissing = notes.Contains(Transcript.DiarizationMissingNote);
        string? text = null;
        var download = false;
        if (notes.Contains(Transcript.CloudDiarizationFailedNote))
        {
            NoticeIcon.Glyph = "";
            text = Loc.T(localMissing
                ? "Cloud diarization failed and no diarization model is installed on this PC."
                : "Cloud diarization failed; speakers were told apart on this PC.");
            download = localMissing;
        }
        else if (localMissing && _model is not null)
        {
            NoticeIcon.Glyph = "";
            var installed = ModelCatalog.Diarization.Any(e => _model.InstalledModelIds.Contains(e.Id));
            text = Loc.T(installed
                ? "Speakers were not told apart. The speaker model is installed now: re-transcribe."
                : "Speakers were not told apart: the speaker model is not installed (Settings › AI Providers › Local AI).");
            download = !installed;
        }
        NoticePanel.Visibility = Shown(text is not null);
        NoticeText.Text = text ?? "";
        NoticeDownloadButton.Visibility = Shown(download);
    }

    /// <summary>
    /// Copy, Re-transcribe (or "Transcribing…" and Stop while a job runs, "Stopping…" once clicked), Summarize, the
    /// video toggle, and playback, the removed-audio note, or both (macOS <c>playbackControls</c>: the transport with
    /// audio, the transport and the note with a video without audio, the note alone with neither).
    /// </summary>
    private void UpdateActions()
    {
        if (_model is null || _transcript is null) return;
        var hasAudio = _transcript.Conversation?.HasAudio ?? false;
        var transcribing = _transcript.IsTranscribing;
        var stopping = _model.IsStopping(_transcript.ConversationId);
        TranscribingPanel.Visibility = Shown(transcribing);
        TranscribingText.Text = Loc.T(stopping ? "Stopping…" : "Transcribing…");
        // Also its only text in the compact row (macOS help(status)).
        SetTip(TranscribingPanel, TranscribingText.Text, ref _transcribingTip);
        StopTranscriptionButton.Visibility = Shown(stopping || _model.CanStopTranscription(_transcript.ConversationId));
        StopTranscriptionButton.IsEnabled = !stopping;
        RetranscribeButton.Visibility = Shown(!transcribing);
        RetranscribeButton.IsEnabled = _transcript.CanRetranscribe;
        SetTip(RetranscribeButton, Loc.T(hasAudio ? "Re-transcribe with another model or language" : "Audio has been removed"), ref _retranscribeTip);
        var hasVideo = HasVideo;
        Player.Visibility = Shown(hasAudio || hasVideo);
        AudioRemovedNote.Visibility = Shown(!hasAudio && _transcript.Conversation is not null);
        SetTip(AudioRemovedNote, Loc.T(hasVideo
            ? "Re-transcribing is no longer possible. The video, transcript, summary and chat stay."
            : "Playback and re-transcribing are no longer possible. The transcript, summary and chat stay."), ref _audioRemovedTip);
        UpdateVideoToggle();
        UpdateSummarize();
    }

    /// <summary>
    /// Show/Hide video with a video only (macOS <c>videoToggle</c>): "Hide video" with the video icon while its pane
    /// shows, "Show video" with the icon struck through while "Hide video" hides it; On or Off for Narrator. Export
    /// video… and Delete video… beside it (macOS <c>exportVideoButton</c>, <c>deleteVideoButton</c>): "Delete video"
    /// without confirmation, both disabled while the conversation is recorded, compressed or transcribed.
    /// </summary>
    private void UpdateVideoToggle()
    {
        if (_model is null) return;
        VideoToggleButton.Visibility = Shown(HasVideo);
        ExportVideoButton.Visibility = Shown(HasVideo);
        DeleteVideoButton.Visibility = Shown(HasVideo);
        var canDelete = _transcript?.Conversation is { } conversation && _model.CanDelete(conversation);
        ExportVideoButton.IsEnabled = canDelete;
        DeleteVideoButton.IsEnabled = canDelete;
        var deleteText = Loc.T(_model.Settings.DeleteWithoutConfirmation ? "Delete video" : "Delete video…");
        AutomationProperties.SetName(DeleteVideoButton, deleteText);
        SetTip(DeleteVideoButton, deleteText, ref _deleteVideoTip);
        var shown = _model.Settings.TranscriptVideoLayout != TranscriptVideoLayout.Hidden;
        var text = Loc.T(shown ? "Hide video" : "Show video");
        VideoToggleSlash.Visibility = Shown(!shown);
        AutomationProperties.SetName(VideoToggleButton, text);
        AutomationProperties.SetItemStatus(VideoToggleButton, Loc.T(shown ? "On" : "Off"));
        SetTip(VideoToggleButton, text, ref _videoToggleTip);
        FitControlRow();
    }

    /// <summary>Shows or hides the video pane (it only does that), saved in the settings (macOS <c>videoToggle</c>).</summary>
    private void ToggleVideo()
    {
        if (_model is null) return;
        var shown = _model.Settings.TranscriptVideoLayout != TranscriptVideoLayout.Hidden;
        _model.ApplySettings(_model.Settings with
        {
            TranscriptVideoLayout = shown ? TranscriptVideoLayout.Hidden : TranscriptVideoLayout.Stacked,
        });
    }

    /// <summary>Opens the export dialog for the conversation's video (macOS <c>exportVideoButton</c>).</summary>
    private async Task ExportVideoAsync()
    {
        if (_model is not { } model || ShowDialog is not { } show || XamlRoot is not { } root
            || _transcript?.Conversation is not { HasVideo: true } conversation || !model.CanDelete(conversation)) return;
        await show(new VideoExportDialog(conversation.Title, conversation.Folder) { XamlRoot = root });
    }

    /// <summary>
    /// Moves the video to the Recycle Bin, asked first like Delete audio unless "Delete conversations without
    /// confirmation" is on (macOS <c>DeleteMenuItems.delete(.video)</c>); the conversation then reloads without it, so the
    /// pane and these buttons go. Checked again once the dialog closes: a job may have started meanwhile.
    /// </summary>
    private async Task DeleteVideoAsync()
    {
        if (_model is not { } model || _transcript?.Conversation is not { HasVideo: true } conversation
            || !model.CanDelete(conversation)) return;
        var id = conversation.Id;
        if (!model.Settings.DeleteWithoutConfirmation)
        {
            if (ShowDialog is not { } show || XamlRoot is not { } root) return;
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = Loc.Format("Delete the video of \"%@\"?", conversation.Title),
                Content = Loc.T("The screen recording is moved to the Recycle Bin. Everything else in the conversation stays."),
                PrimaryButtonText = Loc.T("Delete"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            // The queue throws the result away: Delete is read from its click.
            var confirmed = false;
            dialog.PrimaryButtonClick += (_, _) => confirmed = true;
            if (!await show(dialog) || !confirmed || model.IsClosing) return;
            if (model.Conversations.FirstOrDefault(c => c.Id == id) is not { HasVideo: true } current || !model.CanDelete(current)) return;
        }
        await model.DeleteVideoAsync(id);
    }

    /// <summary>
    /// With lines only (macOS <c>summaryButton</c>): "Summary" once there is one, else "Summarize"; disabled with a
    /// ring and "Summarizing…" while this conversation's summary is written. The title is the tooltip too, the only
    /// text in the compact row.
    /// </summary>
    private void UpdateSummarize()
    {
        if (_model is null || _transcript is null) return;
        var summarizing = _model.AIJobs.EntryFor(_transcript.ConversationId)?.Payload.Kind == AIJobKind.Summary;
        var text = Loc.T(summarizing ? "Summarizing…" : _transcript.Conversation?.HasSummary == true ? "Summary" : "Summarize");
        SummarizeButton.Visibility = Shown(HasSegments);
        SummarizeButton.IsEnabled = !summarizing;
        SummarizingRing.Visibility = Shown(summarizing);
        SummarizingRing.IsActive = summarizing;
        SummarizeText.Text = text;
        AutomationProperties.SetName(SummarizeButton, text);
        SetTip(SummarizeButton, text, ref _summarizeTip);
        FitControlRow();
    }

    /// <summary>
    /// The control row on one row, never wrapped (macOS <c>controlRow(chips:compact:)</c>): Re-transcribe and Summarize
    /// with their titles while they fit beside the narrowest chips ("+N" alone, <see cref="ChipRowPanel"/> fits the
    /// chips to the rest), else as their icons (Re-transcribe's refresh with its chevron, Summarize's document, the
    /// rings alone while a job runs), their titles in the tooltips. Decided from the natural widths, outside the layout
    /// pass (the row's SizeChanged, a button or the chips that changed), so a title shown again to be measured never
    /// makes the layout loop.
    /// </summary>
    private void FitControlRow()
    {
        var width = ControlRow.ActualWidth;
        if (width <= 0) return;
        var key = (width, _chipSpeakers, ChipsPanel.Children.Count, TranscribingPanel.Visibility, TranscribingText.Text,
            RetranscribeButton.Visibility, (SummarizeButton.Visibility, SummarizeText.Text, SummarizingRing.Visibility,
            StopTranscriptionButton.Visibility, VideoToggleButton.Visibility));
        if (key.Equals(_rowFitted)) return;
        _rowFitted = key;
        var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ShowCompactActions(false);
        ControlButtons.Measure(natural);
        ChipsPanel.Measure(natural);
        // Half a pixel of slack: layout rounding may give a little less than the measured width.
        var fits = ControlButtons.DesiredSize.Width + ControlRow.ColumnSpacing + ChipsPanel.MinimumWidth <= width + 0.5;
        if (!fits) ShowCompactActions(true);
        // Measured at their natural width above: the next layout pass measures them with the row's width again.
        ControlRow.InvalidateMeasure();
    }

    /// <summary>Re-transcribe, "Transcribing…" and Summarize with their titles, or as their icons only.</summary>
    private void ShowCompactActions(bool compact)
    {
        RetranscribeText.Visibility = Shown(!compact);
        RetranscribeIcon.Visibility = Shown(compact);
        TranscribingText.Visibility = Shown(!compact);
        SummarizeText.Visibility = Shown(!compact);
        SummarizeIcon.Visibility = Shown(compact && SummarizingRing.Visibility != Visibility.Visible);
    }

    // Menus

    /// <summary>The kept transcripts, newest first, each with its engine and date; the shown one is checked.</summary>
    private void BuildVersionMenu()
    {
        VersionMenu.Items.Clear();
        if (_transcript is null) return;
        foreach (var version in _transcript.Versions)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = version.Details.Length > 0 ? $"{version.Title} · {version.Details}" : version.Title,
                GroupName = "TranscriptVersion",
                IsChecked = version.IsShown,
            };
            var number = version.Number;
            item.Click += (_, _) => _transcript?.ShowVersion(number);
            VersionMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// macOS EngineMenuItems under "Transcribe again with", then the language of the next run with "As recorded" first;
    /// "Download a model…" when no engine is installed or configured.
    /// </summary>
    private void BuildRetranscribeMenu()
    {
        RetranscribeMenu.Items.Clear();
        if (_transcript is not { } transcript || _model is not { } model) return;
        if (!AddEngineItems(RetranscribeMenu, "Transcribe again with", id => _ = transcript.RetranscribeAsync(id))) return;
        RetranscribeMenu.Items.Add(new MenuFlyoutSeparator());
        var language = new MenuFlyoutSubItem { Text = Loc.T("Language") };
        var selection = transcript.RetranscribeLanguage;
        language.Items.Add(LanguageItem(Loc.T("As recorded"), "", selection));
        foreach (var group in LanguageOptions.Groups(selection, model.Settings.RecentLanguages, model.SupportedLanguages()))
        {
            language.Items.Add(new MenuFlyoutSeparator());
            foreach (var code in group) language.Items.Add(LanguageItem(LanguageOptions.Name(code), code, selection));
        }
        RetranscribeMenu.Items.Add(language);
    }

    private RadioMenuFlyoutItem LanguageItem(string text, string code, string selection)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "RetranscribeLanguage", IsChecked = code == selection };
        item.Click += (_, _) =>
        {
            if (_transcript is not null) _transcript.RetranscribeLanguage = code;
        };
        return item;
    }

    private void BuildRetryMenu()
    {
        RetryMenu.Items.Clear();
        AddEngineItems(RetryMenu, "Retry with another engine", id => Transcribe(id));
    }

    /// <summary>
    /// macOS EngineMenuItems: the installed local and configured cloud engines under <paramref name="title"/>, the
    /// default one first, marked "(default)" (<see cref="AppViewModel.InstalledBatchEngines"/>); false without any,
    /// with "Download a model…", which opens AI Providers › Local AI › Transcription (macOS
    /// <c>openLocalAI(.transcription)</c>).
    /// </summary>
    private bool AddEngineItems(MenuFlyout menu, string title, Action<string> pick)
    {
        var engines = _model?.InstalledBatchEngines ?? [];
        if (engines.Count == 0)
        {
            var download = new MenuFlyoutItem { Text = Loc.T("Download a model…") };
            download.Click += (_, _) => _model?.OpenLocalAI(ProvidersSettingsPage.TranscriptionGroup);
            menu.Items.Add(download);
            return false;
        }
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T(title), IsEnabled = false });
        foreach (var engine in engines)
        {
            var item = new MenuFlyoutItem { Text = engine.MenuText };
            var id = engine.Id;
            item.Click += (_, _) => pick(id);
            menu.Items.Add(item);
        }
        return true;
    }

    /// <summary>Transcribe and Retry: with the default engine, or <paramref name="engineId"/>.</summary>
    private void Transcribe(string? engineId)
    {
        if (_model is not null && _transcript is not null) _ = _model.TranscribeAsync(_transcript.ConversationId, engineId);
    }

    /// <summary>
    /// The tab switches in the click's frame, the panel expanded if collapsed; without a summary one starts as the
    /// Summary tab's Summarize starts it, when it could start there (a provider, no other job of this conversation;
    /// other conversations' jobs do not block it).
    /// </summary>
    private void Summarize()
    {
        if (_model is not { } model || _transcript is not { } pane) return;
        model.DetailTab = DetailTab.Summary;
        model.AIPanelCollapsed = false;
        if (pane.Conversation is not { HasSummary: false } conversation || pane.Transcript is not { } transcript
            || model.ProvidersModel.CurrentAIProvider(AIPurpose.Summary) is not { } provider
            || model.AIJobs.EntryFor(conversation.Id) is not null || model.ChatChanging || model.Updater.IsInstalling) return;
        model.StartAIJob(AIJobKind.Summary, null, conversation, transcript, null, [], provider,
            model.ProvidersModel.AIModelFor(provider, AIPurpose.Summary));
    }

    /// <summary>One "Name [mm:ss]: text" line per segment of the shown version.</summary>
    private void Copy()
    {
        if (_transcript is not null) SetClipboard(_transcript.TranscriptText());
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar: could not copy the transcript: {e}");
        }
    }

    // Speakers

    /// <summary>Up to three chips, then "+N" for the others; disabled while a rename could not be saved. The chips that
    /// do not fit the row go behind "+N" too (<see cref="ChipRowPanel"/>, <see cref="OnChipsShown"/>).</summary>
    private void UpdateChips()
    {
        if (_transcript is null) return;
        var speakers = _transcript.Speakers;
        var enabled = _transcript.CanRename;
        if (ReferenceEquals(speakers, _chipSpeakers) && enabled == _chipsEnabled) return;
        _chipSpeakers = speakers;
        _chipsEnabled = enabled;
        ChipsPanel.Children.Clear();
        if (speakers.Count == 0) return;
        foreach (var chip in speakers.Take(MaxSpeakerChips))
        {
            var button = new Button { Content = SpeakerLabel(chip), IsEnabled = enabled };
            AutomationProperties.SetName(button, chip.Name);
            ToolTipService.SetToolTip(button, Loc.T("Rename this speaker"));
            button.Click += (_, _) => OpenRename(chip, button);
            ChipsPanel.Children.Add(button);
        }
        // Always the last child: the panel arranges it only while some speakers have no chip on screen. Its count until
        // the panel tells how many chips show (OnChipsShown), so it is measured with its text from the first.
        var more = new Button { Content = $"+{Math.Max(1, speakers.Count - MaxSpeakerChips)}", IsEnabled = enabled };
        AutomationProperties.SetName(more, Loc.T("Show the other speakers"));
        ToolTipService.SetToolTip(more, Loc.T("Show the other speakers"));
        more.Click += (_, _) => OpenMoreSpeakers(speakers.Skip(_chipsShown).ToList(), more);
        ChipsPanel.Extra = Math.Max(0, speakers.Count - MaxSpeakerChips);
        ChipsPanel.Children.Add(more);
        ChipsPanel.Reset();
        if (!enabled)
        {
            RenameFlyout.Hide();
            MoreSpeakersFlyout.Hide();
        }
    }

    /// <summary>The chips the row has room for: "+N" counts the speakers behind it.</summary>
    private void OnChipsShown(int shown)
    {
        _chipsShown = shown;
        var children = ChipsPanel.Children;
        if (_chipSpeakers is not { } speakers || children.Count == 0 || children[^1] is not Button more) return;
        more.Content = $"+{Math.Max(1, speakers.Count - shown)}";
        // The chips behind "+N" (and "+N" while it is not shown) are not arranged: Tab skips them.
        for (var i = 0; i < children.Count - 1; i++)
            if (children[i] is Control chip) chip.IsTabStop = i < shown;
        more.IsTabStop = ChipsPanel.MoreShows(shown);
    }

    /// <summary>A dot in the speaker's color, then the name.</summary>
    private StackPanel SpeakerLabel(SpeakerChip chip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = "●", Style = SpeakerStyle(chip.ColorIndex), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = chip.Name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void OpenMoreSpeakers(IReadOnlyList<SpeakerChip> hidden, Button target)
    {
        MoreSpeakersList.Children.Clear();
        foreach (var chip in hidden)
        {
            var button = new Button
            {
                Content = SpeakerLabel(chip),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            };
            AutomationProperties.SetName(button, chip.Name);
            ToolTipService.SetToolTip(button, Loc.T("Rename this speaker"));
            button.Click += (_, _) =>
            {
                MoreSpeakersFlyout.Hide();
                OpenRename(chip, target);
            };
            MoreSpeakersList.Children.Add(button);
        }
        MoreSpeakersFlyout.ShowAt(target);
    }

    /// <summary>The speaker's name field ("Name for Speaker 1"), starting with the name the user gave.</summary>
    private void OpenRename(SpeakerChip chip, FrameworkElement target)
    {
        if (_transcript is not { CanRename: true }) return;
        _renaming = chip;
        var title = Loc.Format("Name for %@", chip.DefaultName);
        RenameTitle.Text = title;
        RenameBox.PlaceholderText = chip.DefaultName;
        RenameBox.Text = chip.CustomName;
        AutomationProperties.SetName(RenameBox, title);
        RenameFlyout.ShowAt(target);
    }

    /// <summary>Enter saves (a blank name restores the default); Esc closes the flyout, which cancels.</summary>
    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _renaming is not { } chip || _transcript is not { } transcript) return;
        e.Handled = true;
        var name = RenameBox.Text;
        RenameFlyout.Hide();
        _ = transcript.RenameSpeakerAsync(chip.Speaker, name);
    }

    // Helpers

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetTip(DependencyObject element, string text, ref string? shown)
    {
        if (text == shown) return;
        shown = text;
        ToolTipService.SetToolTip(element, text);
    }
}
