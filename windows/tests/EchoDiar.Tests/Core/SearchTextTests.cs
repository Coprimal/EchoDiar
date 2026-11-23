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
