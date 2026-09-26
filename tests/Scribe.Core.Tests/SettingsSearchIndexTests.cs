using System.Xml.Linq;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsSearchIndexTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly IReadOnlyDictionary<string, string> ControlExclusions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SettingsSearchBox"] = "The global settings search box is the search surface, not a setting.",
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
    [InlineData("playground", "Try dictation box", SettingsPage.TryDictation)]
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
        Assert.Equal(SettingsPage.AiCleanup, results[0].Page);
        Assert.Contains(results, result => result.Label == "Speech model" && result.Page == SettingsPage.Advanced);
        Assert.True(results.TakeWhile(result => result.Label.Contains("Model", StringComparison.OrdinalIgnoreCase)).Any());
    }

    [Fact]
    public void Results_are_read_as_setting_on_page()
    {
        var result = Assert.Single(SettingsSearchIndex.Search("boot"));

        Assert.Equal("Start with Windows on Dictation", result.DisplayText);
    }

    [Fact]
    public void Hidden_targets_carry_the_parent_to_focus()
    {
        var result = SettingsSearchIndex.Search("foundry local").Single(result => result.Label == "On this PC (Foundry Local)");

        Assert.Equal("AiCleanupCheck", result.ParentControlName);
        Assert.Equal("Use AI cleanup", result.ParentLabel);
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
            if (entry.ParentControlName is { } parent)
            {
                Assert.Contains(parent, names);
            }
        }
    }

    [Fact]
    public void Every_setting_control_has_an_index_entry_or_an_exclusion_reason()
    {
        var indexed = SettingsSearchIndex.Entries
            .Select(entry => entry.ControlName)
            .Concat(SettingsSearchIndex.Entries.Select(entry => entry.ParentControlName).Where(name => name is not null)!)
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
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        return XDocument.Load(path, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }
}

