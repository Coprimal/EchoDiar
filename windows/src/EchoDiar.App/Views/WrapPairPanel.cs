using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EchoDiar.App.Views;

/// <summary>
/// Two children on one row, the first at the leading edge and the second in the rest, when the second fits with
/// <see cref="Shrinking"/> (one of its children that trims its text) at least <see cref="MinShrinkingWidth"/> wide; else
/// the second on a row of its own under the first (macOS <c>ViewThatFits</c> fallbacks). Each child keeps its own
/// alignment in its slot. The AI panel's header (the tabs, then the menus, the model menu shrinking first) and the
/// transcript's control row (its buttons, then the speaker chips).
/// </summary>
public sealed partial class WrapPairPanel : Panel
{
    public double Spacing { get; set; } = 8;

    /// <summary>The child of the second that gives up width first; set in code. None: the second keeps its width.</summary>
    public FrameworkElement? Shrinking { get; set; }

    public double MinShrinkingWidth { get; set; } = 96;

    /// <summary>The last measure put the second child beside the first.</summary>
    private bool _oneRow = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return new Size(0, 0);
        var first = Children[0];
        var second = Children[1];
        first.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        // Its natural width first: what the shrinking child may give up shows from it.
        second.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var spare = Shrinking is { } shrinking ? Math.Max(0, shrinking.DesiredSize.Width - MinShrinkingWidth) : 0;
        var width = availableSize.Width;
        // Half a pixel of slack: layout rounding may give a little less than the measured width.
        // An empty second child (no speaker chips) never takes a row of its own.
        var secondEmpty = second.DesiredSize.Width <= 0 || second.DesiredSize.Height <= 0;
        _oneRow = secondEmpty || first.DesiredSize.Width + Spacing + second.DesiredSize.Width - spare <= width + 0.5;
        second.Measure(new Size(_oneRow ? Math.Max(0, width - first.DesiredSize.Width - Spacing) : width, availableSize.Height));
        var used = double.IsInfinity(width) ? first.DesiredSize.Width + Spacing + second.DesiredSize.Width : width;
        return _oneRow
            ? new Size(used, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height))
            : new Size(used, first.DesiredSize.Height + Spacing + second.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2) return finalSize;
        var first = Children[0];
        var second = Children[1];
        var firstWidth = Math.Min(first.DesiredSize.Width, finalSize.Width);
        if (_oneRow)
        {
            first.Arrange(new Rect(0, 0, firstWidth, finalSize.Height));
            var left = firstWidth + Spacing;
            second.Arrange(new Rect(left, 0, Math.Max(0, finalSize.Width - left), finalSize.Height));
        }
        else
        {
            first.Arrange(new Rect(0, 0, firstWidth, first.DesiredSize.Height));
            var top = first.DesiredSize.Height + Spacing;
            second.Arrange(new Rect(0, top, finalSize.Width, second.DesiredSize.Height));
        }
        return finalSize;
    }
}
