using static EchoDiar.Core.Layout.PaneCollapse;

namespace EchoDiar.Tests.Core;

// Guards the drag-to-collapse threshold and that expanding a collapsed panel never breaks a pane minimum.
public sealed class PaneCollapseTests
{
    [Theory]
    [InlineData(861, true)]
    [InlineData(860, false)]
    public void CollapsesBelowHalfTheSecondMinimum(double firstLength, bool collapse)
    {
        Assert.Equal(collapse, ShouldCollapse(1000, firstLength, 280));
    }

    [Theory]
    [InlineData(0.9, 1000, 720)]
    [InlineData(0.9, 500, 250)]
    public void RestoredSplitKeepsTheMinimums(double fraction, double total, double length)
    {
        Assert.Equal(length, Restore(fraction, total, 280, 280));
    }
}
