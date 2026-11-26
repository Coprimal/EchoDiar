using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
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
/// over the gap, it drags or has the keyboard focus. <see cref="Lined"/> shows a line across the whole gap instead
/// (macOS <c>PaneSplit(lined: true)</c>, between the video and the transcript in one card): the divider colour,
/// brighter on the same triggers. Not saved: 50/50 again for each conversation.
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

    private bool _lined;

    /// <summary>The line shown while <see cref="Lined"/>: its root (the whole gap) and the brighter line over the divider.</summary>
    private (Grid Root, UIElement Bright)? _line;

    /// <summary>The content the view set (the grip), kept while <see cref="Lined"/> replaces it.</summary>
    private UIElement? _grip;

    public PaneSplitter()
    {
        // Tab reaches it; a click leaves the focus where it was (the question keeps its cursor).
        IsTabStop = true;
        AllowFocusOnInteraction = false;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Loaded += (_, _) =>
        {
            // After XAML has set the content too, whichever came first.
            ApplyLined();
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
    /// A 1 px line across the whole gap, along it, in the divider colour, the strong stroke colour over it while the
    /// grip would be full (macOS <c>PaneSplit.line</c>), instead of the view's grip; built here, so its brushes are
    /// ThemeResources that follow Light and Dark.
    /// </summary>
    public bool Lined
    {
        get => _lined;
        set
        {
            _lined = value;
            ApplyLined();
            TurnGrip();
            UpdateGrip();
        }
    }

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
    /// The line as the content while <see cref="Lined"/>, the view's grip back otherwise. The line is read from XAML so
    /// its brushes are ThemeResources; its root is transparent, so the whole gap takes the pointer, as the grip's does.
    /// </summary>
    private void ApplyLined()
    {
        if (_lined)
        {
            if (_line is null)
            {
                var root = (Grid)XamlReader.Load(
                    "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='Transparent'>"
                    + "<Rectangle Fill='{ThemeResource DividerStrokeColorDefaultBrush}' />"
                    + "<Rectangle Fill='{ThemeResource ControlStrongStrokeColorDefaultBrush}' Opacity='0' />"
                    + "</Grid>");
                _line = (root, root.Children[1]);
            }
            if (Content != _line.Value.Root)
            {
                _grip = Content;
                Content = _line.Value.Root;
            }
        }
        else if (_line is { } line && Content == line.Root)
        {
            Content = _grip;
            _grip = null;
        }
    }

    /// <summary>
    /// The grip's lines (the shapes of the first <see cref="StackPanel"/> in the content) parallel to the edges the gap
    /// moves: side by side and upright between columns, one over the other and lying between rows (macOS
    /// <c>PaneSplit.grip</c>); with <see cref="Lined"/>, the line upright down the gap or lying across it.
    /// </summary>
    private void TurnGrip()
    {
        if (_lined && _line is { } lined)
        {
            foreach (var shape in lined.Root.Children.OfType<Rectangle>())
            {
                shape.Width = _vertical ? double.NaN : 1;
                shape.Height = _vertical ? 1 : double.NaN;
                shape.HorizontalAlignment = _vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
                shape.VerticalAlignment = _vertical ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            }
            return;
        }
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

    /// <summary>
    /// The grip at full opacity while the gap is hovered, dragged or focused with Tab (with <see cref="Lined"/>, the
    /// brighter line shown over the divider); faded, unless animations are off.
    /// </summary>
    private void UpdateGrip()
    {
        var active = _hovered || _drag is not null || FocusState == FocusState.Keyboard;
        var (target, opacity) = _lined && _line is { } line
            ? (line.Bright, active ? 1.0 : 0)
            : (Content as UIElement, active ? 1.0 : RestOpacity);
        if (target is null) return;
        target.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        target.Opacity = opacity;
    }
}
