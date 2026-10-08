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
        ["ProfileTextFormatCombo"] = "A row editor; the App profiles entry covers text format and Markdown.",
        ["ProfileInjectionCombo"] = "A row editor; the App profiles entry covers typing and paste.",
        ["ProfileShiftEnterCombo"] = "A row editor; the App profiles entry covers typed line breaks and Shift+Enter.",
        ["HistoryRetentionCustomBox"] = "Shown only for Custom in its duration list; the list's entry covers it.",
        ["HistorySearchBox"] = "History's own search box filters shown dictations, not a setting.",
        ["HistoryDetailsText"] = "History details is read-only selected dictation text, not a setting.",
        ["AboutLogsPathBox"] = "About shows the logs path as read-only support information.",
        ["AboutDatabasePathBox"] = "About shows the database path as read-only support information.",
        ["AboutStoreLinkBox"] = "About shows a share link as read-only support information.",
        ["DictionarySearchBox"] = "The Your words tab's own search box filters its list, not a setting.",
        ["LibrarySearchBox"] = "The Word packs tab's own search box finds words, not a setting.",
        ["LibraryDetailRenameBox"] = "Renames the selected word pack; the Word packs entry covers it.",
        ["LibraryUseCheck"] = "Turns the selected word pack on or off; the Word packs entry covers it.",
        ["LibraryAiCheck"] = "Applies to the selected word pack; the Word packs entry covers it.",
        ["WordDetailsSpokenBox"] = "A row editor for the selected word; the Word packs entry covers it.",
        ["WordDetailsWrittenBox"] = "A row editor for the selected word; the Word packs entry covers it.",
        ["WordDetailsWholeWordCheck"] = "A row editor for the selected word; the Word packs entry covers it.",
        ["LocalAppModelBox"] = "Lists the models of the app chosen under On this PC; the Ollama and LM Studio entries land beside it.",
    };

    private static readonly IReadOnlyDictionary<string, string> LabelExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["try.page"] = "The page-level Try dictation destination focuses the test box, whose accessible name is more specific.",
        ["dictionary.words"] = "The destination is a Dictionary tab header inside the TabControl, not the TabControl label.",
        ["dictionary.word-packs"] = "The destination is a Dictionary tab header inside the TabControl, not the TabControl label.",
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
    [InlineData("download ollama", "Model name", SettingsPage.AiCleanup)]
    [InlineData("ollama service", "Start Ollama", SettingsPage.AiCleanup)]
    [InlineData("markdown", "Default text format for app profiles", SettingsPage.Dictation)]
    [InlineData("plain text once", "Use app-aware formatting", SettingsPage.Dictation)]
    [InlineData("profile clipboard", "App profiles", SettingsPage.AppProfiles)]
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
    [InlineData("AiModelBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderLocalRadio", "Radio:LocalAppScribeRadio")]
    [InlineData("LocalAppOllamaRadio", "CheckBox:AiCleanupCheck", "Radio:AiProviderLocalRadio")]
    [InlineData("OllamaDownloadModelBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderLocalRadio", "Radio:LocalAppOllamaRadio")]
    [InlineData("AzureModelBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderFoundryRadio", "Radio:AzureCliRadio", "Action:AzureSignInStatusRow")]
    [InlineData("AzureEndpointBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderFoundryRadio", "View:AzureManualToggleButton")]
    [InlineData("AzureDeploymentBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderFoundryRadio", "View:AzureManualToggleButton")]
    [InlineData("SpClientSecretBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderFoundryRadio", "Radio:AzureServicePrincipalRadio")]
    [InlineData("AzureApiKeyBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderFoundryRadio", "Radio:AzureApiKeyRadio")]
    [InlineData("CustomModelBox", "CheckBox:AiCleanupCheck", "Radio:AiProviderCustomRadio")]
    [InlineData("CopilotModelCombo", "CheckBox:AiCleanupCheck", "Radio:AiProviderCopilotRadio")]
    public void Hidden_targets_carry_the_requirement_chain(string controlName, params string[] requirementControls)
    {
        var entry = SettingsSearchIndex.Entries.Single(entry => entry.ControlName == controlName);
        var actual = entry.Requirements?
            .Select(requirement => $"{requirement.Kind}:{requirement.ControlName}")
            .ToArray();

        Assert.Equal(requirementControls, actual);
    }

    [Fact]
    public void Azure_view_gates_match_azure_settings_access_policy()
    {
        var signedOut = AzureSettingsAccess.Resolve(true, signedIn: false, manualConfigurationRequested: false, hasApiKey: false);
        var signedInClosed = AzureSettingsAccess.Resolve(true, signedIn: true, manualConfigurationRequested: false, hasApiKey: false);
        var signedInOpen = AzureSettingsAccess.Resolve(true, signedIn: true, manualConfigurationRequested: true, hasApiKey: false);

        Assert.False(signedOut.ShowDiscovery);
        Assert.True(signedInClosed.ShowManualToggleButton);
        Assert.False(signedInClosed.ShowEndpointPanel);
        Assert.True(signedInOpen.ShowEndpointPanel);
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
            .Concat(SettingsSearchIndex.Entries.SelectMany(entry => entry.Requirements ?? []).Select(requirement => requirement.ControlName))
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
    public void Entry_labels_follow_the_visible_xaml_label_or_have_a_reason()
    {
        var mismatches = LabelMismatches(LoadXaml());

        Assert.Empty(mismatches);
        Assert.All(LabelExceptions, exception => Assert.False(string.IsNullOrWhiteSpace(exception.Value)));
    }

    [Fact]
    public void Label_test_fails_when_a_bound_label_changes_without_the_index()
    {
        var document = LoadXaml();
        var hotkeyTitle = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "HotkeyTitle");
        hotkeyTitle.SetAttributeValue("Text", "Changed shortcut");

        var mismatches = LabelMismatches(document);

        Assert.Contains(mismatches, mismatch => mismatch.StartsWith("dictation.shortcut:", StringComparison.Ordinal));
    }

    [Fact]
    public void Window_source_overrides_wpf_ui_filtering_and_defers_popup_state()
    {
        var source = SettingsSearchSource();

        Assert.Contains("args.Handled = true;", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Input", source, StringComparison.Ordinal);
        Assert.Contains("No settings found", source, StringComparison.Ordinal);
        Assert.Contains("generation == _settingsSearchGeneration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_source_owns_result_activation_instead_of_suggestion_chosen()
    {
        var source = SettingsSearchSource();

        Assert.Contains("SettingsSearchSuggestionsList_PreviewMouseLeftButtonUp", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchSuggestionsList_PreviewKeyDown", source, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true;", source, StringComparison.Ordinal);
        Assert.Contains("_highlightedSettingsSearchResult = result;", source, StringComparison.Ordinal);
        Assert.Contains("TryGetCurrentResult", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_source_handles_escape_before_wpf_ui_inner_text_box()
    {
        var source = SettingsSearchSource();
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));

        Assert.Contains("PreviewKeyDown=\"SettingsSearchBox_PreviewKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("if (SettingsSearchBox.IsSuggestionListOpen)", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchBox.Text = string.Empty;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_source_names_inner_text_box_and_suggestion_items()
    {
        var source = SettingsSearchSource();
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));

        Assert.Contains("PART_TextBox", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(textBox, \"Find a setting\")", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name\" Value=\"{Binding DisplayText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("public override string ToString() => DisplayText;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_source_waits_for_expanded_targets_to_become_visible()
    {
        var source = SettingsSearchSource();

        Assert.Contains("FocusSearchTargetWhenVisibleAsync", source, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow.AddSeconds(1)", source, StringComparison.Ordinal);
        Assert.Contains("CurrentNavigationPage() == page", source, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> LabelMismatches(XDocument document)
    {
        var elementsByName = document.Descendants()
            .Where(element => element.Attribute(Xaml + "Name") is not null)
            .ToDictionary(element => (string)element.Attribute(Xaml + "Name")!, StringComparer.Ordinal);
        var mismatches = new List<string>();
        foreach (var entry in SettingsSearchIndex.Entries)
        {
            if (LabelExceptions.ContainsKey(entry.Id))
            {
                continue;
            }

            Assert.True(elementsByName.TryGetValue(entry.ControlName, out var element), $"{entry.Id} target missing from XAML.");
            var label = LabelFor(element!, elementsByName);
            Assert.False(string.IsNullOrWhiteSpace(label), $"{entry.Id} has no resolvable XAML label and no exception reason.");
            if (!string.Equals(label, entry.Label, StringComparison.Ordinal))
            {
                mismatches.Add($"{entry.Id}: expected {label}, index {entry.Label}");
            }
        }

        return mismatches;
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
        var labeledBy = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.EndsWith(".LabeledBy", StringComparison.Ordinal))?.Value;
        var labelName = ExtractElementName(labeledBy);
        if (labelName is not null && elementsByName.TryGetValue(labelName, out var labelElement))
        {
            return TextFrom(labelElement);
        }

        return AttributeValue(element, "Content") ??
            AttributeValue(element, "Header") ??
            PreviousText(element) ??
            AttributeValue(element, "AutomationProperties.Name");
    }

    private static string? PreviousText(XElement element)
    {
        foreach (var previous in element.ElementsBeforeSelf().Reverse())
        {
            if (IsDescriptionLike(previous))
            {
                continue;
            }

            var text = TitleTextFrom(previous);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? TitleTextFrom(XElement element)
    {
        if (IsTitleLike(element))
        {
            return TextFrom(element);
        }

        var title = element.Descendants()
            .FirstOrDefault(IsTitleLike);
        return title is not null
            ? TextFrom(title)
            : TextFrom(element) ?? element.Descendants().Select(TextFrom).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsDescriptionLike(XElement element) =>
        element.Name.LocalName == "TextBlock" &&
        (((string?)element.Attribute("Style"))?.Contains("CardDescription", StringComparison.Ordinal) == true ||
         ((string?)element.Attribute("Style"))?.Contains("PageSubtitle", StringComparison.Ordinal) == true);

    private static bool IsTitleLike(XElement element)
    {
        var name = (string?)element.Attribute(Xaml + "Name") ?? string.Empty;
        var style = (string?)element.Attribute("Style") ?? string.Empty;
        return element.Name.LocalName == "TextBlock" &&
            (name.Contains("Title", StringComparison.Ordinal) ||
             name.Contains("Label", StringComparison.Ordinal) ||
             style.Contains("CardTitle", StringComparison.Ordinal));
    }

    private static string? TextFrom(XElement element) =>
        AttributeValue(element, "Text") ?? AttributeValue(element, "Content") ?? AttributeValue(element, "Header");

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

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

    [Fact]
    public void Closing_the_list_on_purpose_keeps_focus_in_the_box()
    {
        // Focus left on the window cancelled the wait for a target inside an opening expander, and made the next Escape
        // close Settings.
        var source = SettingsSearchSource();
        var open = source[source.IndexOf("private void OpenSettingsSearchResult(", StringComparison.Ordinal)..];
        open = open[..open.IndexOf("ShowPage(result.Page);", StringComparison.Ordinal)];
        Assert.Contains("SettingsSearchBox.Focus();", open, StringComparison.Ordinal);

        var escape = source[source.IndexOf("private void SettingsSearchBox_PreviewKeyDown(", StringComparison.Ordinal)..];
        escape = escape[..escape.IndexOf("if (!string.IsNullOrEmpty(SettingsSearchBox.Text))", StringComparison.Ordinal)];
        Assert.Contains("SettingsSearchBox.Focus();", escape, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manual_details_step_counts_as_met_only_when_the_endpoint_shows()
    {
        // Signed out with the Azure CLI, neither the endpoint nor the manual toggle shows; that must route to signing in.
        var source = SettingsSearchSource();
        Assert.Contains("CurrentAzureSettingsAccess.ShowEndpointPanel;", source, StringComparison.Ordinal);
        Assert.Contains("FocusAzureSignInRequirement(requirement with { Kind = SettingsSearchRequirementKind.Action });", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sign_in_hint_goes_once_signed_in_and_content_elements_are_walked_logically()
    {
        var source = SettingsSearchSource();
        Assert.Contains("private void RetireAzureSignInHintIfSignedIn()", source, StringComparison.Ordinal);
        var window = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var apply = window[window.IndexOf("private void ApplyAzureSettingsAccess()", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("RetireAzureSignInHintIfSignedIn();", apply, StringComparison.Ordinal);

        // A Hyperlink has no visual parent; VisualTreeHelper.GetParent throws for it.
        Assert.Contains(": LogicalTreeHelper.GetParent(current))", source, StringComparison.Ordinal);
    }

    private static string SettingsSearchSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.Search.cs"));

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
