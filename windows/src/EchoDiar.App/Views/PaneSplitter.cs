using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The narrow gap between two columns of a <see cref="Grid"/>, or two rows when <see cref="Vertical"/>, that drags to
/// resize them (macOS TranscriptView <c>PaneSplit</c>): it sits in its own column (row), between the two it resizes, and
/// writes their widths (heights) as stars, so they keep their share when the window resizes; each keeps the minimum the
/// view gives (<see cref="Arrange"/>), kept here rather than as the definitions' MinWidth, so a grid without room for
/// both splits half and half instead of overflowing (macOS <c>PaneSplit.clamped</c>). The resize pointer shows over it,
/// ←/→ (↑/↓) move it by <see cref="KeyStep"/> once it has the focus. Its content (set in XAML, so its brushes follow the
/// theme) is the grip, two short lines in the middle, turned across the gap: dimmed at rest, full while the pointer is
/// over the gap, it drags or has the keyboard focus. Not saved: 50/50 again for each conversation.
/// </summary>
public sealed partial class PaneSplitter : UserControl
{
    /// <summary>How far ←/→ move the gap.</summary>
    private const double KeyStep = 20;

    /// <summary>The grip's opacity at rest: its secondary text brush then reads as the strong stroke colour.</summary>
    private const double RestOpacity = 0.7;

    private static readonly UISettings UISettings = new();

    /// <summary>The pointer is over the gap.</summary>
    private bool _hovered;

    /// <summary>Where a drag started (the pointer's x, or y, in the parent grid) and the first pane's length then.</summary>
    private (double Position, double Length)? _drag;

    private bool _vertical;

    /// <summary>The panes' minimums as the view gave them (<see cref="Arrange"/>); null until it does.</summary>
    private (double First, double Second)? _minimums;

    /// <summary>The grid whose size changes <see cref="Clamp"/> follows.</summary>
    private Grid? _followedGrid;

    public PaneSplitter()
    {
        // Tab reaches it; a click leaves the focus where it was (the question keeps its cursor).
        IsTabStop = true;
        AllowFocusOnInteraction = false;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Loaded += (_, _) =>
        {
            TurnGrip();
            UpdateGrip();
            _followedGrid = ParentGrid;
            if (_followedGrid is not null) _followedGrid.SizeChanged += OnGridSizeChanged;
            // Its first size may have come before this.
            Clamp();
        };
        Unloaded += (_, _) =>
        {
            if (_followedGrid is not null) _followedGrid.SizeChanged -= OnGridSizeChanged;
            _followedGrid = null;
        };
        PointerEntered += (_, _) =>
        {
            _hovered = true;
            UpdateGrip();
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            UpdateGrip();
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCanceled += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, e) => EndDrag(e);
        GotFocus += (_, _) => UpdateGrip();
        LostFocus += (_, _) => UpdateGrip();
        KeyDown += OnKeyDown;
    }

    /// <summary>Between two rows of the parent grid (the panes stacked) instead of two columns.</summary>
    public bool Vertical => _vertical;

    /// <summary>
    /// Once the view has put the gap and the panes in the parent grid's columns, or rows when
    /// <paramref name="vertical"/> (the up-down resize pointer, ↑/↓ and the grip's lines across then): the panes'
    /// minimums along that axis.
    /// </summary>
    public void Arrange(bool vertical, double firstMinimum, double secondMinimum)
    {
        if (vertical != _vertical)
        {
            _vertical = vertical;
            ProtectedCursor = InputSystemCursor.Create(vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
            TurnGrip();
        }
        _minimums = (firstMinimum, secondMinimum);
        Clamp();
    }

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => Clamp();

    /// <summary>
    /// The split kept within the minimums as the grid resizes, or half and half while it has no room for both. Sets
    /// the stars only when they change; the grid's size does not depend on them, so the layout never loops.
    /// </summary>
    private void Clamp()
    {
        if (Panes() is not { } panes) return;
        var length = Clamped(panes.First.Length, panes.First.Length + panes.Second.Length);
        if (length is { } clamped && Math.Abs(clamped - panes.First.Length) > 0.5) Resize(clamped);
    }

    private Grid? ParentGrid => Parent as Grid;

    /// <summary>The pointer's position along the axis the gap moves on.</summary>
    private double Along(PointerRoutedEventArgs e, UIElement relativeTo)
    {
        var position = e.GetCurrentPoint(relativeTo).Position;
        return _vertical ? position.Y : position.X;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ParentGrid is not { } grid || Panes() is not { } panes
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer)) return;
        e.Handled = true;
        _drag = (Along(e, grid), panes.First.Length);
        UpdateGrip();
    }

    /// <summary>Relative to the parent grid: the gap itself moves while it drags.</summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || ParentGrid is not { } grid) return;
        e.Handled = true;
        Resize(drag.Length + Along(e, grid) - drag.Position);
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (_drag is null) return;
        _drag = null;
        ReleasePointerCapture(e.Pointer);
        UpdateGrip();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (back, forward) = _vertical ? (VirtualKey.Up, VirtualKey.Down) : (VirtualKey.Left, VirtualKey.Right);
        if ((e.Key != back && e.Key != forward) || Panes() is not { } panes) return;
        e.Handled = true;
        Resize(panes.First.Length + (e.Key == back ? -KeyStep : KeyStep));
    }

    /// <summary>A column, or a row, of the parent grid: its length now, and how it is set.</summary>
    private readonly record struct Pane(double Length, Action<GridLength> SetLength);

    /// <summary>
    /// The columns left and right of the gap's own column, or the rows above and below its row; null when it is not
    /// between two.
    /// </summary>
    private (Pane First, Pane Second)? Panes()
    {
        if (ParentGrid is not { } grid) return null;
        if (_vertical)
        {
            var row = Grid.GetRow(this);
            var rows = grid.RowDefinitions;
            if (row < 1 || row + 1 >= rows.Count) return null;
            return (Of(rows[row - 1]), Of(rows[row + 1]));
        }
        var column = Grid.GetColumn(this);
        var columns = grid.ColumnDefinitions;
        if (column < 1 || column + 1 >= columns.Count) return null;
        return (Of(columns[column - 1]), Of(columns[column + 1]));
    }

    private static Pane Of(ColumnDefinition column) => new(column.ActualWidth, length => column.Width = length);

    private static Pane Of(RowDefinition row) => new(row.ActualHeight, length => row.Height = length);

    /// <summary>
    /// <paramref name="length"/> for the first pane kept between the two minimums out of <paramref name="total"/>;
    /// half when the total is too small for both (macOS <c>PaneSplit.clamped</c>); null before the layout.
    /// </summary>
    private double? Clamped(double length, double total)
    {
        if (total <= 0) return null;
        var (first, second) = _minimums ?? (0, 0);
        if (total < first + second) return total / 2;
        return Math.Max(first, Math.Min(length, total - second));
    }

    /// <summary>
    /// The first pane <paramref name="length"/> long, within both minimums (<see cref="Clamped"/>), the second one the
    /// rest; as stars of their lengths.
    /// </summary>
    private void Resize(double length)
    {
        if (Panes() is not { } panes) return;
        var (first, second) = panes;
        var total = first.Length + second.Length;
        if (Clamped(length, total) is not { } clamped) return;
        first.SetLength(new GridLength(clamped, GridUnitType.Star));
        second.SetLength(new GridLength(Math.Max(total - clamped, 0), GridUnitType.Star));
    }

    /// <summary>
    /// The grip's lines (the shapes of the first <see cref="StackPanel"/> in the content) parallel to the edges the gap
    /// moves: side by side and upright between columns, one over the other and lying between rows (macOS
    /// <c>PaneSplit.grip</c>).
    /// </summary>
    private void TurnGrip()
    {
        var lines = Content as StackPanel ?? (Content as Panel)?.Children.OfType<StackPanel>().FirstOrDefault();
        if (lines is null) return;
        lines.Orientation = _vertical ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var line in lines.Children.OfType<FrameworkElement>())
        {
            var (thin, length) = (Math.Min(line.Width, line.Height), Math.Max(line.Width, line.Height));
            line.Width = _vertical ? length : thin;
            line.Height = _vertical ? thin : length;
        }
    }

    /// <summary>The grip at full opacity while the gap is hovered, dragged or focused with Tab; faded, unless animations are off.</summary>
    private void UpdateGrip()
    {
        if (Content is not UIElement grip) return;
        grip.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        grip.Opacity = _hovered || _drag is not null || FocusState == FocusState.Keyboard ? 1 : RestOpacity;
    }
}
