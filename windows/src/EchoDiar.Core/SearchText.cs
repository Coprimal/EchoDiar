using System.Globalization;

namespace EchoDiar.Core;

/// <summary>
/// The app's text search (macOS <c>SearchText</c>), ignoring case and accents (Swift <c>localizedStandardContains</c>;
/// "đ" is a letter of its own). A query is found as a whole, or word by word: each query word inside a word of the text,
/// and with a typo or two (<see cref="AllowedEdits"/>) only when asked for. The filters and the transcript search allow
/// typos only when nothing is found without them, so a correct word never lets look-alikes in (folded Vietnamese
/// syllables are often one letter apart).
/// </summary>
public static class SearchText
{
    /// <summary>True when <paramref name="text"/> contains <paramref name="term"/> ignoring case and accents.</summary>
    public static bool Contains(string text, string term) => ContainsFolded(text, Fold(term));

    /// <summary><see cref="Contains"/> with a term already passed through <see cref="Fold"/>.</summary>
    internal static bool ContainsFolded(string text, string foldedTerm) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(Fold(text), foldedTerm, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>
    /// Every occurrence of <paramref name="term"/> (trimmed) in <paramref name="text"/>, in order, not overlapping, as
    /// <see cref="Contains"/> matches; none for a blank term. The catch-up search highlights these (macOS
    /// <c>SearchText.ranges</c>). <see cref="Fold"/> keeps every index, so the ranges are those of the text.
    /// </summary>
    public static List<(int Start, int Length)> Ranges(string text, string term)
    {
        List<(int Start, int Length)> found = [];
        var folded = Fold(term.Trim());
        if (folded.Length == 0) return found;
        var source = Fold(text).AsSpan();
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        for (var start = 0; start < source.Length;)
        {
            var index = compare.IndexOf(source[start..], folded, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var length);
            if (index < 0 || length == 0) break;
            found.Add((start + index, length));
            start += index + length;
        }
        return found;
    }

    // Filters

    /// <summary>
    /// The words of some texts, folded once (macOS <c>SearchText.Words</c>): a filter that runs on every keystroke reads
    /// long texts (transcripts, chats) through these, built when the texts are read.
    /// </summary>
    public sealed class Words
    {
        private readonly HashSet<string> _set = new(StringComparer.Ordinal);

        public Words(IEnumerable<string> texts)
        {
            foreach (var text in texts)
            {
                foreach (var (start, length) in WordRanges(text)) _set.Add(FoldWord(text.Substring(start, length)));
            }
        }

        /// <summary>Some word has <paramref name="term"/> inside it, or is a typo away from it when <paramref name="typos"/>.</summary>
        internal bool Contains(string term, bool typos) => _set.Contains(term) || _set.Any(word => WordMatches(word, term, typos));
    }

    /// <summary>
    /// True when <paramref name="query"/> is blank, some text contains it whole, or each of its words is inside a word of
    /// the texts (<paramref name="words"/>, else the texts' own), in any order; a word may be a typo away when
    /// <paramref name="typos"/> (macOS <c>SearchText.matches</c>).
    /// </summary>
    public static bool Matches(string query, IReadOnlyList<string> texts, IReadOnlyList<Words>? words = null, bool typos = false)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        var key = Fold(query);
        if (texts.Any(text => ContainsFolded(text, key))) return true;
        var terms = Terms(query);
        if (terms.Count == 0) return false;
        words ??= [new Words(texts)];
        return terms.All(term => words.Any(w => w.Contains(term, typos)));
    }

    /// <summary>
    /// The items <paramref name="matches"/> (given an item and whether typos are allowed) accepts without typos; with
    /// them only when none is. All for a blank query (macOS <c>SearchText.filter</c>).
    /// </summary>
    public static List<T> Filter<T>(IEnumerable<T> items, string query, Func<T, bool, bool> matches)
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        var exact = list.Where(item => matches(item, false)).ToList();
        if (exact.Count > 0 || query.Trim().Length == 0) return exact;
        return list.Where(item => matches(item, true)).ToList();
    }

    /// <summary>
    /// What a filter's highlight marks in <paramref name="text"/> (macOS <c>SearchText.matchRanges</c>): the query and
    /// each of its words wherever they occur; when the text matches only with typos, also the words a typo away. In
    /// order, overlapping ranges merged. <paramref name="typos"/> decides it for a text cut in pieces (a rendered text's
    /// runs).
    /// </summary>
    public static List<(int Start, int Length)> MatchRanges(string text, string query, bool? typos = null)
    {
        var found = new[] { query }.Concat(WordRanges(query).Select(w => query.Substring(w.Start, w.Length)))
            .SelectMany(term => Ranges(text, term)).ToList();
        if (typos ?? !Matches(query, [text]))
        {
            var terms = Terms(query);
            found.AddRange(WordRanges(text).Where(w =>
            {
                var word = FoldWord(text.Substring(w.Start, w.Length));
                return terms.Any(term => WordMatches(word, term, typos: true));
            }));
        }
        return Merged(found);
    }

    // Transcript

    /// <summary>
    /// The transcript search (macOS <c>SearchText.phraseRanges</c>): what <see cref="Ranges"/> finds, plus runs of
    /// consecutive words matching the query's words in order, each word with its query word inside it, or a typo away
    /// when <paramref name="typos"/>. In order, overlapping ranges merged; none for a blank query.
    /// </summary>
    public static List<(int Start, int Length)> PhraseRanges(string text, string query, bool typos)
    {
        var found = Ranges(text, query);
        var terms = Terms(query);
        if (terms.Count > 0)
        {
            var words = WordRanges(text);
            var folded = words.Select(w => FoldWord(text.Substring(w.Start, w.Length))).ToList();
            for (var start = 0; start + terms.Count <= words.Count;)
            {
                var matched = true;
                for (var i = 0; i < terms.Count && matched; i++) matched = WordMatches(folded[start + i], terms[i], typos);
                if (!matched)
                {
                    start++;
                    continue;
                }
                var last = words[start + terms.Count - 1];
                found.Add((words[start].Start, last.Start + last.Length - words[start].Start));
                start += terms.Count;
            }
        }
        return Merged(found);
    }

    // Words

    /// <summary>Typos a query word may have: none up to 4 letters (short words, Vietnamese syllables, are too alike), 1 up to 7, then 2.</summary>
    private static int AllowedEdits(int length) => length <= 4 ? 0 : length <= 7 ? 1 : 2;

    /// <summary><paramref name="term"/> is inside <paramref name="word"/>, or a typo away from it when <paramref name="typos"/>.</summary>
    private static bool WordMatches(string word, string term, bool typos)
    {
        if (word.Contains(term, StringComparison.Ordinal)) return true;
        var allowed = typos ? AllowedEdits(term.Length) : 0;
        return allowed > 0 && Math.Abs(word.Length - term.Length) <= allowed && EditDistance(word, term) <= allowed;
    }

    /// <summary>Levenshtein distance.</summary>
    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 0; i < a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i + 1;
            for (var j = 0; j < b.Length; j++)
                current[j + 1] = Math.Min(Math.Min(previous[j + 1] + 1, current[j] + 1), previous[j] + (a[i] == b[j] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }

    /// <summary>The query's words, folded.</summary>
    private static List<string> Terms(string query) =>
        WordRanges(query).Select(w => FoldWord(query.Substring(w.Start, w.Length))).ToList();

    /// <summary>Runs of letters and digits (with their combining marks).</summary>
    private static List<(int Start, int Length)> WordRanges(string text)
    {
        List<(int Start, int Length)> found = [];
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || char.IsSurrogate(text[i])
                || CharUnicodeInfo.GetUnicodeCategory(text[i]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
            if (inWord && start < 0) start = i;
            else if (!inWord && start >= 0)
            {
                found.Add((start, i - start));
                start = -1;
            }
        }
        return found;
    }

    /// <summary>Lowercased without accents; "đ" is not decomposed, so it stays a letter of its own.</summary>
    private static string FoldWord(string word)
    {
        var builder = new System.Text.StringBuilder(word.Length);
        foreach (var c in word.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    private static List<(int Start, int Length)> Merged(IEnumerable<(int Start, int Length)> ranges)
    {
        List<(int Start, int Length)> merged = [];
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start < merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, range.Start + range.Length) - last.Start);
            }
            else merged.Add(range);
        }
        return merged;
    }

    /// <summary>
    /// ICU's <c>IgnoreNonSpace</c> folds "đ"/"Đ" into "d", but it is a letter of its own in Vietnamese and Swift keeps it
    /// apart ("da nang" does not find "Đà Nẵng"; "đà nẵng" does). Both cases become one private-use character, which never
    /// occurs in titles, transcripts, summaries, chats or settings, so they match each other and nothing else.
    /// </summary>
    internal static string Fold(string s) => s.Replace('đ', '').Replace('Đ', '');
}
