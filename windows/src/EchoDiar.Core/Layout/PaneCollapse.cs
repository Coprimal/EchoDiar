namespace EchoDiar.Core.Layout;

/// <summary>
/// The collapse rule of a two-pane split whose second pane (the AI or recording panel) folds into a rail: when a drag
/// collapses it and where expanding puts the split back (macOS <c>PaneSplit</c> in <c>TranscriptView.swift</c>).
/// </summary>
public static class PaneCollapse
{
    /// <summary>
    /// Whether the second pane collapses with the first one <paramref name="firstLength"/> long out of
    /// <paramref name="total"/>: when less than half its minimum would be left (macOS
    /// <c>total - (start + moved) &lt; secondMinimum / 2</c>). False before the layout (<paramref name="total"/> &lt;= 0).
    /// </summary>
    public static bool ShouldCollapse(double total, double firstLength, double secondMinimum) =>
        total > 0 && total - firstLength < secondMinimum / 2;

    /// <summary>
    /// The first pane's length for the split <paramref name="fraction"/> (0..1) remembered while collapsed, kept
    /// between both minimums out of <paramref name="total"/>; half when the total is too small for both (as
    /// <c>PaneSplitter.Clamped</c> and macOS <c>PaneSplit.clamped</c>). 0 before the layout (<paramref name="total"/>
    /// &lt;= 0).
    /// </summary>
    public static double Restore(double fraction, double total, double firstMinimum, double secondMinimum)
    {
        if (total <= 0) return 0;
        if (total < firstMinimum + secondMinimum) return total / 2;
        return Math.Max(firstMinimum, Math.Min(fraction * total, total - secondMinimum));
    }
}
