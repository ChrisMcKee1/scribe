using System.Xml.Linq;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsSearchIndexTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly IReadOnlyDictionary<string, string> ControlExclusions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SettingsSearchBox"] = "The global settings search box is the search surface, not a setting.",
        ["ModeCombo"] = "Part of its shortcut row; the shortcut's entry lands beside it.",
        ["DictationOnlyModeCombo"] = "Part of its shortcut row; the shortcut's entry lands beside it.",
        ["IdleReleaseCustomBox"] = "Shown only for Custom in its duration list; the list's entry covers it.",
        ["MaxDictationCustomBox"] = "Shown only for Custom in its duration list; the list's entry covers it.",
        ["SnippetEnabledCheck"] = "A row editor; the page's entry covers it.",
        ["SnippetPhraseBox"] = "A row editor; the page's entry covers it.",
        ["SnippetTemplateBox"] = "A row editor; the page's entry covers it.",
        ["ProfileNameBox"] = "A row editor; the page's entry covers it.",
        ["ProfileProcessesBox"] = "A row editor; the page's entry covers it.",
        ["ProfileStyleBox"] = "A row editor; the page's entry covers it.",
        ["ProfileNewlineCombo"] = "A row editor; the page's entry covers it.",
        ["HistoryRetentionCustomBox"] = "Shown only for Custom in its duration list; the list's entry covers it.",
        ["HistorySearchBox"] = "History's own search box filters shown dictations, not a setting.",
        ["HistoryDetailsText"] = "History details is read-only selected dictation text, not a setting.",
        ["AboutLogsPathBox"] = "About shows the logs path as read-only support information.",
        ["AboutDatabasePathBox"] = "About shows the database path as read-only support information.",
        ["AboutStoreLinkBox"] = "About shows a share link as read-only support information.",
    };

    [Theory]
    [InlineData("hotkey", "Dictation shortcut", SettingsPage.Dictation)]
    [InlineData("shortcut", "Dictation shortcut", SettingsPage.Dictation)]
    [InlineData("key", "Dictation shortcut", SettingsPage.Dictation)]
    [InlineData("VAD", "Trim silence", SettingsPage.Advanced)]
    [InlineData("silence", "Stop when I stop talking", SettingsPage.Dictation)]
    [InlineData("overlay", "Show the recording indicator", SettingsPage.Dictation)]
    [InlineData("pill", "Show the recording indicator", SettingsPage.Dictation)]
    [InlineData("library", "Word packs", SettingsPage.Dictionary)]
    [InlineData("libraries", "Word packs", SettingsPage.Dictionary)]
    [InlineData("vocabulary", "Word packs", SettingsPage.Dictionary)]
    [InlineData("playground", "Try dictation", SettingsPage.TryDictation)]
    [InlineData("model", "Model", SettingsPage.AiCleanup)]
    [InlineData("startup", "Start with Windows", SettingsPage.Dictation)]
    [InlineData("boot", "Start with Windows", SettingsPage.Dictation)]
    public void Synonyms_find_the_expected_setting(string query, string label, SettingsPage page)
    {
        var results = SettingsSearchIndex.Search(query);

        Assert.Contains(results, result => result.Label == label && result.Page == page);
    }

    [Fact]
    public void Search_is_case_accent_insensitive_and_word_prefix_based()
    {
        Assert.Contains(SettingsSearchIndex.Search("Cópi"), result => result.Label == "GitHub Copilot");
        Assert.Contains(SettingsSearchIndex.Search("rec ind"), result => result.Label == "Show the recording indicator");
        Assert.Empty(SettingsSearchIndex.Search("zzzz"));
    }

    [Fact]
    public void Search_ranks_label_matches_before_keywords_and_caps_results()
    {
        var results = SettingsSearchIndex.Search("model", maxResults: 20);

        Assert.True(results.Count <= 8);
        Assert.Equal("Model", results[0].Label);
        Assert.Equal("On this PC", results[0].Context);
        Assert.Contains(results, result => result.DisplayText == "Speech model on Advanced");
        Assert.True(results.TakeWhile(result => result.Label.Contains("Model", StringComparison.OrdinalIgnoreCase)).Any());
    }

    [Fact]
    public void Results_are_read_as_setting_context_on_page()
    {
        var result = Assert.Single(SettingsSearchIndex.Search("boot"));
        var model = SettingsSearchIndex.Search("foundry model").First(result => result.ControlName == "AzureModelBox");

        Assert.Equal("Start with Windows on Dictation", result.DisplayText);
        Assert.Equal("Model (Microsoft Foundry) on AI cleanup", model.DisplayText);
    }

    [Fact]
    public void No_two_entries_have_the_same_display_text()
    {
        var duplicates = SettingsSearchIndex.Entries
            .GroupBy(entry => $"{entry.DisplayLabel} on {entry.PageLabel}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("AiModelBox", "AiCleanupCheck", "AiProviderLocalRadio")]
    [InlineData("AzureModelBox", "AiCleanupCheck", "AiProviderFoundryRadio", "AzureCliRadio")]
    [InlineData("SpClientSecretBox", "AiCleanupCheck", "AiProviderFoundryRadio", "AzureServicePrincipalRadio")]
    [InlineData("AzureApiKeyBox", "AiCleanupCheck", "AiProviderFoundryRadio", "AzureApiKeyRadio")]
    [InlineData("CustomModelBox", "AiCleanupCheck", "AiProviderCustomRadio")]
    [InlineData("CopilotModelCombo", "AiCleanupCheck", "AiProviderCopilotRadio")]
    public void Hidden_targets_carry_the_requirement_chain(string controlName, params string[] requirementControls)
    {
        var entry = SettingsSearchIndex.Entries.Single(entry => entry.ControlName == controlName);

        Assert.Equal(requirementControls, entry.Requirements?.Select(requirement => requirement.ControlName).ToArray());
    }

    [Fact]
    public void Row_editors_are_not_returned_as_results()
    {
        Assert.DoesNotContain(SettingsSearchIndex.Search("snippet"), result => result.DisplayText.StartsWith("On on", StringComparison.Ordinal));
        Assert.Contains(SettingsSearchIndex.Search("snippet"), result => result.DisplayText == "Voice snippets on Voice snippets");
        Assert.Contains(SettingsSearchIndex.Search("profile"), result => result.DisplayText == "App profiles on App profiles");
    }

    [Fact]
    public void Every_index_entry_names_a_control_in_the_settings_window()
    {
        var names = LoadXaml().Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in SettingsSearchIndex.Entries)
        {
            Assert.Contains(entry.ControlName, names);
            foreach (var requirement in entry.Requirements ?? [])
            {
                Assert.Contains(requirement.ControlName, names);
            }
        }
    }

    [Fact]
    public void Every_setting_control_has_an_index_entry_or_an_exclusion_reason()
    {
        var indexed = SettingsSearchIndex.Entries
            .Select(entry => entry.ControlName)
            .Concat(SettingsSearchIndex.Entries.SelectMany(entry => entry.Requirements ?? [] ).Select(requirement => requirement.ControlName))
            .ToHashSet(StringComparer.Ordinal);
        var missing = LoadXaml().Descendants()
            .Where(IsSettingControl)
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !indexed.Contains(name!) && !ControlExclusions.ContainsKey(name!))
            .ToArray();

        Assert.Empty(missing);
        Assert.All(ControlExclusions, exclusion => Assert.False(string.IsNullOrWhiteSpace(exclusion.Value)));
    }

    [Fact]
    public void Entry_labels_follow_the_visible_labeled_by_text_when_present()
    {
        var document = LoadXaml();
        var elementsByName = document.Descendants()
            .Where(element => element.Attribute(Xaml + "Name") is not null)
            .ToDictionary(element => (string)element.Attribute(Xaml + "Name")!, StringComparer.Ordinal);

        foreach (var entry in SettingsSearchIndex.Entries)
        {
            if (!elementsByName.TryGetValue(entry.ControlName, out var element))
            {
                continue;
            }

            var expected = LabelFor(element, elementsByName);
            if (expected is null)
            {
                continue;
            }

            Assert.Equal(expected, entry.Label);
        }
    }

    [Fact]
    public void Window_source_overrides_wpf_ui_filtering_and_defers_popup_state()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.Search.cs"));

        Assert.Contains("args.Handled = true;", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Input", source, StringComparison.Ordinal);
        Assert.Contains("No settings found", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_source_keeps_arrow_highlights_separate_from_clicks_and_enter()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.Search.cs"));

        Assert.Contains("sender.IsSuggestionListOpen && Mouse.LeftButton != MouseButtonState.Pressed", source, StringComparison.Ordinal);
        Assert.Contains("_highlightedSettingsSearchResult = result;", source, StringComparison.Ordinal);
        Assert.Contains("_highlightedSettingsSearchResult is { } highlighted", source, StringComparison.Ordinal);
    }

    private static bool IsSettingControl(XElement element)
    {
        var name = (string?)element.Attribute(Xaml + "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return element.Name.LocalName is "CheckBox" or "ComboBox" or "NumberBox" or "TextBox" or "PasswordBox" or "RadioButton" or "ToggleSwitch";
    }

    private static string? LabelFor(XElement element, IReadOnlyDictionary<string, XElement> elementsByName)
    {
        var labeledBy = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "LabeledBy")?.Value;
        var labelName = ExtractElementName(labeledBy);
        if (labelName is not null && elementsByName.TryGetValue(labelName, out var labelElement))
        {
            return (string?)labelElement.Attribute("Text") ?? (string?)labelElement.Attribute("Content") ?? (string?)labelElement.Attribute("Header");
        }

        return (string?)element.Attribute("Content") ?? (string?)element.Attribute("Header");
    }

    private static string? ExtractElementName(string? binding)
    {
        const string marker = "ElementName=";
        if (string.IsNullOrWhiteSpace(binding))
        {
            return null;
        }

        var start = binding.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = binding.IndexOfAny([',', '}'], start);
        return end < 0 ? binding[start..].Trim() : binding[start..end].Trim();
    }

    private static XDocument LoadXaml()
    {
        var path = Path.GetFullPath(Path.Combine(
            RepositoryRoot(),
            "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        return XDocument.Load(path, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
