using EchoDiar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

/// <summary>
/// The model and language menus of the AI panel's Chat and Summary tabs, of the library chat and of the recording's right
/// panel (macOS <c>AIModelMenu</c> and <c>AILanguageMenu</c>, shared by AIPanelView.swift, GlobalChatView.swift and
/// RecordingViews.swift), and the setup hint shown while no model can run. Built when they open, from the providers
/// model. The model menu shows and edits one <see cref="AIPurpose"/>'s default. Also what the headers they sit in
/// share: the narrowing steps (<see cref="FitRow"/>), icon-only tabs and narrower menus.
/// </summary>
internal static class AIMenus
{
    /// <summary>The summary languages offered (macOS <c>AILanguageMenu</c>), plus a hand-edited one.</summary>
    private static readonly (string Code, string Name)[] Languages =
        [("auto", "Auto"), ("en", "English"), ("fr", "French"), ("vi", "Vietnamese")];

    /// <summary>
    /// The model button's label for <paramref name="purpose"/>: an in-app model's name, else the picked model, else the
    /// provider's name.
    /// </summary>
    public static string ModelTitle(ProvidersViewModel providers, AIPurpose purpose)
    {
        var current = providers.CurrentAIProvider(purpose);
        if (current is null) return Loc.T("No AI model");
        if (current.Kind == AIProviderKind.InApp) return current.Name;
        return providers.AIModelFor(current, purpose) is { Length: > 0 } model ? model : current.Name;
    }

    /// <summary>Why Windows AI cannot run, under the setup hint; null when it is hidden or ready.</summary>
    public static string? WindowsAIHint(ProvidersViewModel providers) =>
        providers.WindowsAI is { Hidden: false, Reason: { } reason } ? Loc.T(reason) : null;

    /// <summary>
    /// Every provider with the models it offers (macOS <c>AIModelMenu</c>): on this PC first, then the hosts, then the
    /// ones whose data leaves this PC; a provider without a model says to choose one in Settings, one that cannot run now
    /// is off with why; then "Choose Models…" (<paramref name="chooseModels"/>: Settings › AI Providers › Cloud). Checks
    /// and picks <paramref name="purpose"/>'s default; for <see cref="AIPurpose.CatchUp"/>, no in-app provider (the live
    /// transcriber holds the model during a recording).
    /// </summary>
    public static void FillModelMenu(MenuFlyout menu, ProvidersViewModel providers, AIPurpose purpose, Action chooseModels)
    {
        menu.Items.Clear();
        var all = purpose == AIPurpose.CatchUp
            ? providers.AIProviders.Where(p => p.Kind != AIProviderKind.InApp).ToList()
            : providers.AIProviders;
        var current = providers.CurrentAIProvider(purpose);
        var currentModel = current is null ? null : providers.AIModelFor(current, purpose);
        var inApp = all.Where(p => p.Kind == AIProviderKind.InApp).ToList();
        var hosts = all.Where(p => p.Kind != AIProviderKind.InApp && !p.LeavesPC).Concat(all.Where(p => p.LeavesPC)).ToList();
        if (inApp.Count > 0)
        {
            menu.Items.Add(Header(Loc.T("On this PC")));
            foreach (var provider in inApp)
            {
                menu.Items.Add(ModelItem(providers, purpose, provider, provider.DefaultModel, provider.Name, current, currentModel));
            }
        }
        foreach (var provider in hosts)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Header(provider.LeavesPC ? Loc.Format("%@ (leaves this PC)", provider.Name) : provider.Name));
            if (provider.Models.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("No model: choose one in Settings"), IsEnabled = false });
            foreach (var id in provider.Models)
            {
                menu.Items.Add(ModelItem(providers, purpose, provider, id, id.Length == 0 ? Loc.T("Default") : id, current, currentModel));
            }
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var choose = new MenuFlyoutItem { Text = Loc.T("Choose Models…") };
        choose.Click += (_, _) => chooseModels();
        menu.Items.Add(choose);
    }

    /// <summary>Auto, English, French, Vietnamese, and a hand-edited code (macOS <c>AILanguageMenu</c>).</summary>
    public static void FillLanguageMenu(MenuFlyout menu, ProvidersViewModel providers, string selection) =>
        FillLanguageMenu(menu.Items, providers, selection);

    /// <summary>The same languages into <paramref name="items"/>: a menu's, or a submenu's (the AI panel's "⋮").</summary>
    public static void FillLanguageMenu(IList<MenuFlyoutItemBase> items, ProvidersViewModel providers, string selection)
    {
        items.Clear();
        var choices = Languages.Select(l => (l.Code, Name: Loc.T(l.Name))).ToList();
        if (choices.All(c => c.Code != selection)) choices.Add((selection, selection));
        foreach (var (code, name) in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "SummaryLanguage", IsChecked = code == selection };
            item.Click += (_, _) =>
            {
                if (providers.SummaryLanguage != code) providers.SummaryLanguage = code;
            };
            items.Add(item);
        }
    }

    /// <summary>
    /// The first of <paramref name="steps"/> narrowing steps (macOS <c>ViewThatFits</c>) whose row fits
    /// <paramref name="width"/>: <paramref name="apply"/> sets a step, then the row's <paramref name="leading"/> and
    /// <paramref name="trailing"/> parts are measured at their natural width, with the <paramref name="row"/>'s column
    /// spacing (a step may change it) between them; the last step when none fits. Called
    /// outside the layout pass, so a label shown again to be measured never makes the layout loop; the caller then
    /// invalidates the row's measure.
    /// </summary>
    public static void FitRow(double width, Grid row, FrameworkElement leading, FrameworkElement trailing, int steps, Action<int> apply)
    {
        var natural = new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        for (var step = 0; step < steps; step++)
        {
            apply(step);
            if (step == steps - 1) return;
            leading.Measure(natural);
            trailing.Measure(natural);
            // Half a pixel of slack: layout rounding may give a little less than the measured width.
            if (leading.DesiredSize.Width + row.ColumnSpacing + trailing.DesiredSize.Width <= width + 0.5) return;
        }
    }

    /// <summary>
    /// A header tab's name, or only its icon (macOS <c>tabIcon</c>), the name then its tooltip; the name is what screen
    /// readers say either way.
    /// </summary>
    public static void ShowTabLabel(SelectorBarItem tab, string title, string glyph, bool label)
    {
        AutomationProperties.SetName(tab, title);
        if (label)
        {
            tab.Icon = null;
            tab.Text = title;
            ToolTipService.SetToolTip(tab, null);
            return;
        }
        tab.Text = "";
        if (tab.Icon is not FontIcon icon || icon.Glyph != glyph)
        {
            tab.Icon = new FontIcon
            {
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 14,
                Glyph = glyph,
            };
        }
        ToolTipService.SetToolTip(tab, title);
    }

    /// <summary>
    /// A menu's chevron 4 px from its icon while it shows only its icon (macOS: 6 → 3 pt), else the 8 px of the
    /// DropDownButton template's <c>ChevronIcon</c>. Nothing before the template is applied.
    /// </summary>
    public static void TightenChevron(DropDownButton button, bool iconOnly)
    {
        if (Descendant(button, "ChevronIcon") is { } chevron) chevron.Margin = new Thickness(iconOnly ? 4 : 8, 0, 0, 0);
    }

    private static FrameworkElement? Descendant(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var childName } element && childName == name) return element;
            if (Descendant(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// A header button's or tab's side padding 4 px narrower in the narrowest header (macOS: the menus' and the tabs'
    /// 12 pt padding down to 8), else its style's.
    /// </summary>
    public static void CompactPadding(Control button, bool compact)
    {
        button.ClearValue(Control.PaddingProperty);
        if (!compact) return;
        var padding = button.Padding;
        button.Padding = new Thickness(Math.Max(padding.Left - 4, 0), padding.Top, Math.Max(padding.Right - 4, 0), padding.Bottom);
    }

    private static MenuFlyoutItem Header(string text) => new() { Text = text, IsEnabled = false, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private static RadioMenuFlyoutItem ModelItem(
        ProvidersViewModel providers, AIPurpose purpose, AIProviderInfo provider, string id, string title, AIProviderInfo? current,
        string? currentModel)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = title,
            GroupName = "AIModel",
            IsChecked = current?.Id == provider.Id && currentModel == id,
            IsEnabled = provider.UnavailableReason is null,
        };
        if (provider.UnavailableReason is { } reason) ToolTipService.SetToolTip(item, reason);
        item.Click += (_, _) => providers.SetAIModel(id, provider, purpose);
        return item;
    }
}
