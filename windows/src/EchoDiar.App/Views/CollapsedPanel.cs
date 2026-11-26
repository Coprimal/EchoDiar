using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

/// <summary>
/// The AI panel, or the recording's side panel, collapsed beside the transcript (under it when <see cref="Vertical"/>)
/// (macOS <c>CollapsedPanel</c>): a card <see cref="Thickness"/> wide (tall when stacked) with an icon button per tab,
/// which asks to open the panel on that tab (<see cref="TabInvoked"/>), and a chevron that asks to open it on the first
/// tab (<see cref="ExpandInvoked"/>), where the collapse button was: on top beside the transcript, at the trailing end
/// under it. The card is read from XAML, so its brushes are ThemeResources that follow Light and Dark.
/// </summary>
public sealed partial class CollapsedPanel : UserControl
{
    /// <summary>
    /// Its width beside the transcript (its height under it): the panel header's 32 px tabs track and the 10 px padding
    /// around it, so the rail's buttons line up with the header's (macOS <c>CollapsedPanel.thickness</c>).
    /// </summary>
    public const double Thickness = ButtonSize + 2 * HeaderPadding;

    /// <summary>The header controls' height, which the buttons take.</summary>
    private const double ButtonSize = 32;

    /// <summary>The header's padding: the rail's along it, so its first button starts where the header's does.</summary>
    private const double HeaderPadding = 10;

    /// <summary>The padding across the rail: the header's less the card's 1 px stroke, which takes layout room here.</summary>
    private const double CrossPadding = HeaderPadding - 1;

    /// <summary>The card, its content rebuilt by <see cref="Rebuild"/>.</summary>
    private readonly Border _card;

    private IReadOnlyList<(object Tab, string Glyph, string Title)> _tabs = [];

    private bool _vertical;

    public CollapsedPanel()
    {
        _card = (Border)XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'"
            + " Background='{ThemeResource CardBackgroundFillColorDefaultBrush}'"
            + " BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' />");
        // The panels' corner radius (XamlControlsResources); 8 if it is ever missing, rather than a crash.
        _card.CornerRadius = Application.Current.Resources.TryGetValue("OverlayCornerRadius", out var radius)
            && radius is CornerRadius corner ? corner : new CornerRadius(8);
        Content = _card;
        Rebuild();
    }

    /// <summary>Under the transcript (the panes stacked): a row along the bottom instead of a column.</summary>
    public bool Vertical
    {
        get => _vertical;
        set
        {
            if (value == _vertical) return;
            _vertical = value;
            Rebuild();
        }
    }

    /// <summary>A tab's button was clicked: open the panel on it.</summary>
    public event Action<object>? TabInvoked;

    /// <summary>The chevron was clicked: open the panel on its first tab.</summary>
    public event Action? ExpandInvoked;

    /// <summary>The tabs shown, in order: each one's Segoe Fluent glyph and its title (tooltip and name).</summary>
    public void SetTabs(IReadOnlyList<(object Tab, string Glyph, string Title)> tabs)
    {
        _tabs = tabs;
        Rebuild();
    }

    /// <summary>
    /// Side by side, a column from the top: the chevron, then the tabs. Stacked, a row: the tabs on the left, the
    /// chevron at the trailing end.
    /// </summary>
    private void Rebuild()
    {
        var expand = IconButton(_vertical ? "\uE70E" : "\uE76B", Loc.T("Expand panel"));
        expand.Click += (_, _) => ExpandInvoked?.Invoke();
        var tabs = new StackPanel { Orientation = _vertical ? Orientation.Horizontal : Orientation.Vertical, Spacing = 4 };
        foreach (var (tab, glyph, title) in _tabs)
        {
            var button = IconButton(glyph, title);
            button.Click += (_, _) => TabInvoked?.Invoke(tab);
            tabs.Children.Add(button);
        }
        if (_vertical)
        {
            var row = new Grid
            {
                Padding = new Microsoft.UI.Xaml.Thickness(HeaderPadding, CrossPadding, HeaderPadding, CrossPadding),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tabs.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(expand, 1);
            row.Children.Add(tabs);
            row.Children.Add(expand);
            _card.Child = row;
        }
        else
        {
            var column = new StackPanel
            {
                Spacing = 4,
                Padding = new Microsoft.UI.Xaml.Thickness(CrossPadding, HeaderPadding, CrossPadding, HeaderPadding),
                VerticalAlignment = VerticalAlignment.Top,
            };
            column.Children.Add(expand);
            column.Children.Add(tabs);
            _card.Child = column;
        }
    }

    /// <summary>A header-sized icon button, its tooltip and accessible name <paramref name="title"/>.</summary>
    private static Button IconButton(string glyph, string title)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = ButtonSize,
            Height = ButtonSize,
            Padding = new Microsoft.UI.Xaml.Thickness(0),
            Content = new FontIcon
            {
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 14,
                Glyph = glyph,
            },
        };
        ToolTipService.SetToolTip(button, title);
        AutomationProperties.SetName(button, title);
        return button;
    }
}
