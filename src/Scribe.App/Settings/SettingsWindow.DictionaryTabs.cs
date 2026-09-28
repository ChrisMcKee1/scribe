using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Scribe.App.Infrastructure;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

// The Dictionary page's two tabs: each shows how much it holds, and each tab's line links to the other, so neither tab
// is easy to miss and the difference between them is said where people look (DictionaryTabsText holds the words).
public partial class SettingsWindow
{
    // Kept current from what the page shows: every rows change of either list reaches RefreshDictionaryStatus, and the
    // word packs list is rebuilt through RefreshWordPackList, which calls this too.
    private void UpdateDictionaryTabSummaries()
    {
        if (YourWordsTab is null || WordPacksTab is null)
        {
            return;
        }

        var words = DictionaryTabsText.YourWordsSummary(_dictionaryLoad.IsLoaded ? _rows.Count : null);
        TabHeader.SetSummary(YourWordsTab, words);
        AutomationProperties.SetHelpText(YourWordsTab, DictionaryTabsText.HelpText(words, DictionaryTabsText.YourWordsGuide));

        var packsOn = 0;
        foreach (var row in _libraryRows)
        {
            if (row.Enabled)
            {
                packsOn++;
            }
        }

        var packs = DictionaryTabsText.WordPacksSummary(_wordPackWorkspace is null ? null : packsOn, _libraryRows.Count);
        TabHeader.SetSummary(WordPacksTab, packs);
        AutomationProperties.SetHelpText(WordPacksTab, DictionaryTabsText.HelpText(packs, DictionaryTabsText.WordPacksGuide));
    }

    private void YourWordsToWordPacksLink_Click(object sender, RoutedEventArgs e) => OpenDictionaryTab(WordPacksTab);

    private void WordPacksToYourWordsLink_Click(object sender, RoutedEventArgs e) => OpenDictionaryTab(YourWordsTab);

    // The link sits on the tab it leaves, so keyboard focus moves to the tab it opens, which a screen reader then reads with
    // its summary and line.
    private void OpenDictionaryTab(TabItem tab)
    {
        DictionaryTabs.SelectedItem = tab;
        tab.Focus();
    }
}
