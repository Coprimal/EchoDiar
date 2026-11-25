using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EchoDiar.App.Views;

/// <summary>
/// The transcript's speaker chips on one row: as many as fit the width it gets, the others behind the last child, the
/// "+N" button (macOS <c>controlRow(chips:)</c>, which tries 3, 2, 1 then 0 chips). Without a limit (its natural size)
/// every chip shows. Tells <see cref="ShownChanged"/> how many chips show, so "+N" counts the others.
/// </summary>
public sealed partial class ChipRowPanel : Panel
{
    public double Spacing { get; set; } = 6;

    /// <summary>The speakers that never get a chip (past the three); "+N" shows while there are any.</summary>
    public int Extra { get; set; }

    /// <summary>The chips shown by the last measure with a width; -1 before the first.</summary>
    private int _shown = -1;

    /// <summary>The chips shown changed; the handler updates "+N" (the last child) and its list.</summary>
    public event Action<int>? ShownChanged;

    /// <summary>The narrowest the row gets, from the last measure: "+N" alone, or nothing without chips.</summary>
    public double MinimumWidth => Children.Count == 0 ? 0 : Children[Children.Count - 1].DesiredSize.Width;

    /// <summary>Whether "+N" shows with <paramref name="shown"/> chips on screen.</summary>
    public bool MoreShows(int shown) => shown < Children.Count - 1 || Extra > 0;

    /// <summary>After the children were rebuilt: the next measure reports its count again.</summary>
    public void Reset()
    {
        _shown = -1;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        if (count == 0) return new Size(0, 0);
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (var child in Children) child.Measure(infinite);
        var chips = count - 1;
        var more = Children[count - 1];
        var height = 0.0;
        foreach (var child in Children) height = Math.Max(height, child.DesiredSize.Height);
        // The natural size (TranscriptView.FitControlRow asks it): every chip, and nothing reported, or "+N" would flip
        // between the natural and the fitted count on every layout pass.
        if (double.IsInfinity(availableSize.Width)) return new Size(RowWidth(chips, more), height);
        // Fewer chips until the row fits; "+N" shows whenever a chip is left out, or for the speakers past three.
        var shown = chips;
        while (shown > 0 && RowWidth(shown, more) > availableSize.Width + 0.5) shown--;
        if (shown != _shown)
        {
            _shown = shown;
            ShownChanged?.Invoke(shown);
        }
        return new Size(RowWidth(shown, more), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0) return finalSize;
        var more = Children[count - 1];
        // Never measured with a width (an unbounded parent): every chip.
        var shown = _shown < 0 ? count - 1 : _shown;
        var x = 0.0;
        for (var i = 0; i < count - 1; i++)
        {
            var child = Children[i];
            if (i < shown)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }
        more.Arrange(MoreShows(shown) ? new Rect(x, 0, more.DesiredSize.Width, finalSize.Height) : new Rect(0, 0, 0, 0));
        return finalSize;
    }

    /// <summary>The first <paramref name="shown"/> chips and, when it shows, "+N", with the spacing between them.</summary>
    private double RowWidth(int shown, UIElement more)
    {
        var width = 0.0;
        var items = 0;
        for (var i = 0; i < shown; i++)
        {
            width += Children[i].DesiredSize.Width;
            items++;
        }
        if (MoreShows(shown))
        {
            width += more.DesiredSize.Width;
            items++;
        }
        return width + Math.Max(0, items - 1) * Spacing;
    }

}
