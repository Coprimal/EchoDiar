using EchoDiar.AI;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

/// <summary>
/// What an answer's links do. <see cref="References"/> are a library answer's sources (null for an answer about one
/// conversation, whose <c>[mm:ss]</c> citations call <see cref="Seek"/>); only their ids open a conversation. A link
/// whose action is null shows as plain text. <see cref="OpenUrl"/> gets a checked http(s) URL and should show its
/// destination before opening it (the link text is model output), as macOS does.
/// </summary>
public sealed record MarkdownLinks(
    IReadOnlyList<ChatLog.Reference>? References = null,
    Action<double>? Seek = null,
    Action<Guid, double?>? OpenConversation = null,
    Action<Uri>? OpenUrl = null);

/// <summary>
/// The AI panel's search marks in one text (macOS <c>MatchMarks</c>): the matches' ranges in its plain text
/// (<see cref="MarkdownParser.Plain"/>, or a question as written), and the index of the current one.
/// </summary>
public sealed record MatchMarks(IReadOnlyList<(int Start, int Length)> Ranges, int? Current)
{
    public static readonly MatchMarks None = new([], null);

    public bool SameAs(MatchMarks other) => Current == other.Current && Ranges.SequenceEqual(other.Ranges);
}

/// <summary>
/// Model Markdown in a <see cref="RichTextBlock"/> (macOS <c>MarkdownText</c>): the lines and styles of
/// <see cref="MarkdownParser"/>, built from Paragraph, Run and Hyperlink objects only, so the text is never read as
/// XAML, HTML or an image source. Hyperlinks have no NavigateUri: a click calls the <see cref="MarkdownLinks"/> action,
/// so the framework never launches anything itself. Each call rebuilds the whole text (a parse is linear, but the
/// selection is lost), so a streamed answer should be rendered at most a few times a second plus once at its end.
/// </summary>
public static class MarkdownView
{
    /// <summary>The indent of a list level, as macOS (14 points).</summary>
    private const double Indent = 14;

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    /// <summary>The search marks' colors, as the transcript's (TranscriptView <c>MatchBrush</c>, <c>CurrentMatchBrush</c>).</summary>
    private static readonly global::Windows.UI.Color MatchColor = ColorHelper.FromArgb(0x66, 0xFF, 0xD6, 0x00);
    private static readonly global::Windows.UI.Color CurrentMatchColor = ColorHelper.FromArgb(0xCC, 0xFF, 0x9A, 0x00);

    /// <summary>
    /// Renders <paramref name="markdown"/>, the matches of <paramref name="highlight"/> (the catch-up search's query,
    /// <see cref="SearchText.MatchRanges"/>; none when empty) highlighted in each run of the rendered text.
    /// </summary>
    public static void Render(RichTextBlock target, string markdown, MarkdownLinks links, string highlight = "")
    {
        target.IsTextSelectionEnabled = true;
        target.TextHighlighters.Clear();
        target.Blocks.Clear();
        foreach (var block in MarkdownParser.Parse(markdown, links.References))
            target.Blocks.Add(ToParagraph(block, links));
        if (highlight.Length == 0) return;
        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(Colors.Yellow),
            Foreground = new SolidColorBrush(Colors.Black),
        };
        var runs = target.Blocks.OfType<Paragraph>().SelectMany(p => Runs(p.Inlines)).ToList();
        // Typo words only when the whole text matches only with them (macOS: matchRanges on the rendered text).
        var typos = !SearchText.Matches(highlight, [string.Join("\n", runs.Select(r => r.Text))]);
        foreach (var run in runs)
        {
            foreach (var (start, length) in SearchText.MatchRanges(run.Text, highlight, typos))
                highlighter.Ranges.Add(new TextRange { StartIndex = run.ContentStart.Offset + start, Length = length });
        }
        if (highlighter.Ranges.Count > 0) target.TextHighlighters.Add(highlighter);
    }

    /// <summary>
    /// Shows <paramref name="marks"/> (the AI panel's search) in <paramref name="target"/>, rendered by <see cref="Render"/>
    /// or a question's one run, in place of its earlier marks; the highlight stays. As the transcript's: the find yellow
    /// for the matches, orange with dark text for the current one.
    /// </summary>
    public static void Mark(RichTextBlock target, MatchMarks marks)
    {
        foreach (var old in target.TextHighlighters.Where(h => h.Background is SolidColorBrush { Color: var c } && (c == MatchColor || c == CurrentMatchColor)).ToList())
            target.TextHighlighters.Remove(old);
        if (marks.Ranges.Count == 0) return;
        var others = new TextHighlighter { Background = new SolidColorBrush(MatchColor) };
        var current = new TextHighlighter { Background = new SolidColorBrush(CurrentMatchColor), Foreground = new SolidColorBrush(Colors.Black) };
        var runs = PlainRuns(target);
        for (var i = 0; i < marks.Ranges.Count; i++)
        {
            var (start, length) = marks.Ranges[i];
            var highlighter = i == marks.Current ? current : others;
            // A match across runs (a bold word, a link) is marked in each.
            foreach (var (plain, run) in runs)
            {
                var from = Math.Max(start, plain);
                var to = Math.Min(start + length, plain + run.Text.Length);
                if (from < to) highlighter.Ranges.Add(new TextRange { StartIndex = run.ContentStart.Offset + from - plain, Length = to - from });
            }
        }
        if (others.Ranges.Count > 0) target.TextHighlighters.Add(others);
        if (current.Ranges.Count > 0) target.TextHighlighters.Add(current);
    }

    /// <summary>Where <paramref name="offset"/> of the plain text is in <paramref name="target"/>, to scroll a match into view.</summary>
    public static TextPointer? Position(RichTextBlock target, int offset)
    {
        foreach (var (plain, run) in PlainRuns(target))
        {
            if (offset >= plain && offset < plain + run.Text.Length)
                return run.ContentStart.GetPositionAtOffset(offset - plain, LogicalDirection.Forward);
        }
        return null;
    }

    /// <summary>
    /// The runs of <paramref name="target"/>, each with where its text starts in the plain text
    /// (<see cref="MarkdownParser.Plain"/>): the paragraphs' runs in order, a line break between paragraphs.
    /// </summary>
    private static List<(int Plain, Run Run)> PlainRuns(RichTextBlock target)
    {
        List<(int, Run)> runs = [];
        var position = -1;
        foreach (var paragraph in target.Blocks.OfType<Paragraph>())
        {
            position++;
            foreach (var run in Runs(paragraph.Inlines))
            {
                runs.Add((position, run));
                position += run.Text.Length;
            }
        }
        return runs;
    }

    /// <summary>The runs of <paramref name="inlines"/>, also those inside a hyperlink.</summary>
    private static IEnumerable<Run> Runs(IEnumerable<Inline> inlines) =>
        inlines.SelectMany(inline => inline switch
        {
            Run run => [run],
            Span span => Runs(span.Inlines),
            _ => Enumerable.Empty<Run>(),
        });

    private static Paragraph ToParagraph(MarkdownBlock block, MarkdownLinks links)
    {
        var heading = block.Kind == MarkdownBlockKind.Heading;
        var list = block.Kind is MarkdownBlockKind.Bullet or MarkdownBlockKind.Numbered;
        var paragraph = new Paragraph { Margin = new Thickness(list ? block.Level * Indent : 0, heading ? 4 : 0, 0, 4) };
        if (heading) paragraph.FontWeight = FontWeights.SemiBold;
        var prefix = MarkdownParser.Prefix(block);
        if (prefix.Length > 0) paragraph.Inlines.Add(new Run { Text = prefix });
        foreach (var inline in block.Inlines) paragraph.Inlines.Add(ToInline(inline, links));
        return paragraph;
    }

    private static Inline ToInline(MarkdownInline inline, MarkdownLinks links)
    {
        var run = new Run { Text = inline.Text };
        if (inline.Style.HasFlag(MarkdownStyle.Bold)) run.FontWeight = FontWeights.Bold;
        if (inline.Style.HasFlag(MarkdownStyle.Italic)) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (inline.Style.HasFlag(MarkdownStyle.Code)) run.FontFamily = CodeFont;
        if (LinkAction(inline.Link, links) is not { } action) return run;
        var hyperlink = new Hyperlink();
        hyperlink.Inlines.Add(run);
        hyperlink.Click += (_, _) => action();
        return hyperlink;
    }

    /// <summary>
    /// The click action of an allowed link, checked again here: a web URL must still be http(s) without user info, and a
    /// reference must be one of the answer's own.
    /// </summary>
    private static Action? LinkAction(MarkdownLink? link, MarkdownLinks links) => link switch
    {
        MarkdownLink.Web web when links.OpenUrl is { } open && MarkdownParser.WebUrl(web.Url.OriginalString) is { } url =>
            () => open(url),
        MarkdownLink.Seek seek when links.Seek is { } seekTo && links.References is null
            && seek.Seconds is >= 0 and <= MarkdownParser.MaxSeconds =>
            () => seekTo(seek.Seconds),
        MarkdownLink.Reference reference when links.OpenConversation is { } openConversation
            && links.References?.Any(r => r.ConversationID == reference.Id) == true =>
            () => openConversation(reference.Id, reference.Seconds),
        _ => null,
    };
}
