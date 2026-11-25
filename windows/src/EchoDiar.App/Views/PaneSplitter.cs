using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The narrow gap between two columns of a <see cref="Grid"/> that drags to resize them (macOS TranscriptView
/// <c>PaneSplit</c>): it sits in its own column, between the two it resizes, and writes their widths as stars, so they
/// keep their share when the window resizes; each keeps its <see cref="ColumnDefinition.MinWidth"/>. The resize pointer
/// shows over it, ←/→ move it by <see cref="KeyStep"/> once it has the focus. Its content (set in XAML, so its brushes
/// follow the theme) is the grip, two short lines in the middle: dimmed at rest, full while the pointer is over the gap,
/// it drags or has the keyboard focus. Not saved: 50/50 again for each conversation.
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

    /// <summary>Where a drag started (the pointer's x in the parent grid) and the left column's width then.</summary>
    private (double X, double Width)? _drag;

    public PaneSplitter()
    {
        // Tab reaches it; a click leaves the focus where it was (the question keeps its cursor).
        IsTabStop = true;
        AllowFocusOnInteraction = false;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Loaded += (_, _) => UpdateGrip();
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

    private Grid? ParentGrid => Parent as Grid;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ParentGrid is not { } grid || Columns() is not { } columns
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer)) return;
        e.Handled = true;
        _drag = (e.GetCurrentPoint(grid).Position.X, columns.Left.ActualWidth);
        UpdateGrip();
    }

    /// <summary>Relative to the parent grid: the gap itself moves while it drags.</summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || ParentGrid is not { } grid) return;
        e.Handled = true;
        Resize(drag.Width + e.GetCurrentPoint(grid).Position.X - drag.X);
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
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right) || Columns() is not { } columns) return;
        e.Handled = true;
        Resize(columns.Left.ActualWidth + (e.Key == VirtualKey.Left ? -KeyStep : KeyStep));
    }

    /// <summary>The columns left and right of the gap's own column; null when it is not between two.</summary>
    private (ColumnDefinition Left, ColumnDefinition Right)? Columns()
    {
        var column = Grid.GetColumn(this);
        if (ParentGrid?.ColumnDefinitions is not { } columns || column < 1 || column + 1 >= columns.Count) return null;
        return (columns[column - 1], columns[column + 1]);
    }

    /// <summary>
    /// The left column <paramref name="width"/> wide, within both minimums, the right one the rest; as stars of their
    /// widths. When the two minimums do not fit, the left one wins (the grid lays them out as it can).
    /// </summary>
    private void Resize(double width)
    {
        if (Columns() is not { } columns) return;
        var (left, right) = columns;
        var total = left.ActualWidth + right.ActualWidth;
        if (total <= 0) return;
        var clamped = Math.Max(left.MinWidth, Math.Min(width, total - right.MinWidth));
        left.Width = new GridLength(clamped, GridUnitType.Star);
        right.Width = new GridLength(Math.Max(total - clamped, 0), GridUnitType.Star);
    }

    /// <summary>The grip at full opacity while the gap is hovered, dragged or focused with Tab; faded, unless animations are off.</summary>
    private void UpdateGrip()
    {
        if (Content is not UIElement grip) return;
        grip.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        grip.Opacity = _hovered || _drag is not null || FocusState == FocusState.Keyboard ? 1 : RestOpacity;
    }
}
