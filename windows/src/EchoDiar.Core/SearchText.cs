using System.Globalization;

namespace EchoDiar.Core;

/// <summary>
/// Swift <c>localizedStandardContains</c>: the text search of the conversations and of Settings, ignoring case and
/// accents.
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

    /// <summary>
    /// ICU's <c>IgnoreNonSpace</c> folds "đ"/"Đ" into "d", but it is a letter of its own in Vietnamese and Swift keeps it
    /// apart ("da nang" does not find "Đà Nẵng"; "đà nẵng" does). Both cases become one private-use character, which never
    /// occurs in titles, transcripts, summaries, chats or settings, so they match each other and nothing else.
    /// </summary>
    internal static string Fold(string s) => s.Replace('đ', '').Replace('Đ', '');
}
