using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private sealed record NavigationItem(
        SettingsPage Page,
        string Label,
        string Group,
        int Position,
        bool IsFirstInGroup,
        SymbolRegular Icon);

    private static SymbolRegular IconFor(SettingsPage page) => page switch
    {
        SettingsPage.Dictation => SymbolRegular.Mic24,
        SettingsPage.TryDictation => SymbolRegular.Beaker24,
        SettingsPage.AiCleanup => SymbolRegular.Wand24,
        SettingsPage.Dictionary => SymbolRegular.Book24,
        SettingsPage.VoiceSnippets => SymbolRegular.TextQuote24,
        SettingsPage.AppProfiles => SymbolRegular.AppsList24,
        SettingsPage.History => SymbolRegular.History24,
        SettingsPage.Usage => SymbolRegular.DataTrending24,
        SettingsPage.Advanced => SymbolRegular.WrenchScrewdriver24,
        SettingsPage.Diagnostics => SymbolRegular.Pulse24,
        SettingsPage.About => SymbolRegular.Info24,
        _ => SymbolRegular.Settings24,
    };

    private IReadOnlyList<Grid> AllSectionPanels =>
    [
        SectionDictation,
        SectionAdvanced,
        SectionAi,
        SectionDictionary,
        SectionVoiceSnippets,
        SectionAppProfiles,
        SectionTryDictation,
        SectionHistory,
        SectionUsage,
        SectionDiagnostics,
        SectionAbout,
    ];

    private IReadOnlyDictionary<SettingsPage, Grid> PagePanels => new Dictionary<SettingsPage, Grid>
    {
        [SettingsPage.Dictation] = SectionDictation,
        [SettingsPage.TryDictation] = SectionTryDictation,
        [SettingsPage.AiCleanup] = SectionAi,
        [SettingsPage.Dictionary] = SectionDictionary,
        [SettingsPage.VoiceSnippets] = SectionVoiceSnippets,
        [SettingsPage.AppProfiles] = SectionAppProfiles,
        [SettingsPage.History] = SectionHistory,
        [SettingsPage.Usage] = SectionUsage,
        [SettingsPage.Advanced] = SectionAdvanced,
        [SettingsPage.Diagnostics] = SectionDiagnostics,
        [SettingsPage.About] = SectionAbout,
    };

    private void InitializeNavigation()
    {
        var items = SettingsNavigation.Items
            .Select(item => new NavigationItem(item.Page, item.Label, item.Group, item.Position, item.IsFirstInGroup, IconFor(item.Page)))
            .ToArray();
        var view = CollectionViewSource.GetDefaultView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.Group)));
        NavList.SelectedValuePath = nameof(NavigationItem.Page);
        NavList.ItemsSource = view;
        NavList.SelectedValue = SettingsPage.Dictation;
        RefreshFirstRunHint();
    }

    private SettingsPage? CurrentNavigationPage()
    {
        if (NavList.SelectedValue is SettingsPage page)
        {
            return page;
        }

        return NavList.SelectedItem is NavigationItem item ? item.Page : null;
    }
}
