using EchoDiar.Core;

namespace EchoDiar.Tests.Core;

// Guards the catch-up search highlight: every occurrence of the term, ignoring case and accents, in order; "đ" is a
// letter of its own, not "d" with an accent (as the conversation search). macOS SearchTextTests.
public sealed class SearchTextTests
{
    private const string Text = "Nẵng said: Đà NANG, not da nang.";

    [Fact]
    public void RangesOfEveryOccurrenceIgnoringCaseAndAccents()
    {
        Assert.Equal(["Nẵng", "NANG", "nang"], Found("nang"));
        Assert.Equal(["Đà"], Found("đà"));
        Assert.Equal(["da"], Found("da"));
    }

    [Fact]
    public void BlankOrMissingTermFindsNothing()
    {
        Assert.Empty(Found("  "));
        Assert.Empty(Found("kangaroo"));
    }

    private static string[] Found(string term) =>
        SearchText.Ranges(Text, term).Select(r => Text.Substring(r.Start, r.Length)).ToArray();
}

// Guards the transcript search: what Ranges finds, plus words with a query word inside them or, when asked for, a typo
// or two away (none for words of 4 letters or fewer), and a query of several words only as consecutive words. macOS
// PhraseSearchTextTests.
public sealed class PhraseSearchTextTests
{
    private const string Text = "The Meeting about the project deadline moved; Đà Nẵng team said the project is late.";

    [Fact]
    public void FindsEveryPlainOccurrence()
    {
        Assert.Equal(["Nẵng", "NANG", "nang"], Found("nang", "Nẵng said: Đà NANG, not da nang."));
        Assert.Equal(["da"], Found("da", "Nẵng said: Đà NANG, not da nang."));
    }

    [Fact]
    public void ToleratesTyposInLongerWords()
    {
        Assert.Equal(["Meeting"], Found("meetng"));
        Assert.Equal(["deadline"], Found("deadlnie"));
        Assert.Equal(["project", "project"], Found("projet"));
    }

    [Fact]
    public void ShortWordsMustMatchExactly()
    {
        Assert.Empty(Found("tema"));
        Assert.Equal(["Nẵng"], Found("nang"));
    }

    [Fact]
    public void SeveralWordsMatchOnlyConsecutiveWords()
    {
        Assert.Equal(["project deadline"], Found("project deadlne"));
        Assert.Equal(["project deadline"], Found("proj dead"));
        Assert.Empty(Found("project late"));
    }

    [Fact]
    public void BlankOrMissingQueryFindsNothing()
    {
        Assert.Empty(Found("  "));
        Assert.Empty(Found("kangaroo"));
    }

    [Fact]
    public void TyposOnlyWhenAskedFor()
    {
        Assert.Empty(Found("meetng", typos: false));
        Assert.Equal(["thành"], Found("thamh", "Kế hoạch thành phố, tháng sau"));
    }

    [Fact]
    public void FindAcrossTextsWithTyposOnlyWhenNoneMatches()
    {
        string[] texts = ["Kế hoạch thành phố", "tháng sau", "thành công"];
        string[] FoundIn(string query) =>
            SearchText.Find(query, texts).Select(m => $"{m.Text}:{texts[m.Text].Substring(m.Start, m.Length)}").ToArray();
        Assert.Equal(["0:thành", "2:thành"], FoundIn("thanh"));
        Assert.Equal(["0:thành", "2:thành"], FoundIn("thamh"));
        Assert.Empty(FoundIn("  "));
    }

    private static string[] Found(string query, string text = Text, bool typos = true) =>
        SearchText.PhraseRanges(text, query, typos).Select(r => text.Substring(r.Start, r.Length)).ToArray();
}

// Guards the filters (conversations, chats, catch-ups, Settings, models): every query word inside some word of the
// texts, in any order; typos only when asked for, which the filters do only when nothing matches without them
// (Filter), so a correct word never lets look-alikes in; the highlight marks typo words only then. macOS
// FilterSearchTextTests.
public sealed class FilterSearchTextTests
{
    private static readonly string[] Texts = ["Weekly sync", "The meeting approved the budget."];

    [Fact]
    public void EveryWordSomewhereInAnyOrder()
    {
        Assert.True(SearchText.Matches("budget weekly", Texts));
        Assert.True(SearchText.Matches("MEET", Texts));
        Assert.True(SearchText.Matches("  ", Texts));
        Assert.False(SearchText.Matches("budget kangaroo", Texts));
    }

    [Fact]
    public void TyposOnlyWhenAllowed()
    {
        Assert.False(SearchText.Matches("budgat", Texts));
        Assert.True(SearchText.Matches("budgat", Texts, typos: true));
        Assert.False(SearchText.Matches("tema", Texts, typos: true));
    }

    [Fact]
    public void PrecomputedWordsServeLongTexts()
    {
        var words = new SearchText.Words(["The meeting approved the budget."]);
        Assert.True(SearchText.Matches("budget weekly", ["Weekly sync"], [new SearchText.Words(["Weekly sync"]), words]));
        Assert.False(SearchText.Matches("budget weekly", ["Weekly sync"], [new SearchText.Words(["Weekly sync"])]));
    }

    [Fact]
    public void FilterAllowsTyposOnlyWhenNothingMatchesWithout()
    {
        string[] items = ["Kế hoạch thành phố", "Tháng sau"];
        Assert.Equal(["Kế hoạch thành phố"], SearchText.Filter(items, "thanh", (item, typos) => SearchText.Matches("thanh", [item], typos: typos)));
        Assert.Equal(["Kế hoạch thành phố"], SearchText.Filter(items, "thamh", (item, typos) => SearchText.Matches("thamh", [item], typos: typos)));
        Assert.Equal(items, SearchText.Filter(items, "  ", (item, typos) => SearchText.Matches("  ", [item], typos: typos)));
    }

    [Fact]
    public void MatchRangesMarkWhatMatched()
    {
        const string text = "Weekly meeting: budget";
        string[] Found(string query) => SearchText.MatchRanges(text, query).Select(r => text.Substring(r.Start, r.Length)).ToArray();
        Assert.Equal(["Weekly", "budget"], Found("budget weekly"));
        Assert.Equal(["meet"], Found("meet"));
        Assert.Equal(["budget"], Found("budgat"));
        Assert.Empty(Found("tema"));
    }
}
