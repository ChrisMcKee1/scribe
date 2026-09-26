using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The Settings save's draft (round 4) covers what a save stores, pinned by source because the window has no tests of its
/// own. A value the save stores that its draft does not read would be closed over when it changed during the save's wait.
/// These fail when a future change adds a stored value, an editor that applies on focus loss, or an unframed value, without
/// carrying it in the draft.
/// </summary>
public sealed class SaveDraftCoverageTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] WalkedPages = ["SectionGeneral", "SectionDictation", "SectionOverlay", "SectionAi"];

    // The kinds of editor the draft's walk reads (by XAML element name), or containers of them.
    private static readonly string[] WalkedKinds = ["TextBox", "PasswordBox", "NumberBox", "ToggleSwitch", "CheckBox", "RadioButton", "ComboBox", "Slider"];

    // Values the save computes beyond a plain editor, each read by the draft through the same expression the save uses.
    private static readonly Dictionary<string, string> Computed = new()
    {
        ["Hotkey"] = "_pendingBinding with { Mode = SelectedMode }",
        ["DictationOnlyHotkey"] = "_pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode }",
        ["EnableAiCleanup"] = "_externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true)",
        ["AiCleanupAzureSubscriptionId"] = Subscription,
        ["AiCleanupAzureSubscriptionName"] = Subscription,
        ["AiCleanupAzureSubscriptionTenantId"] = Subscription,
        ["Profiles"] = "BuildProfiles()",
        ["EnabledDictionaryLibraryIds"] = "CollectEnabledLibraryIds()",
    };

    // Values the save stores that nothing edited during its wait can change, with the reason.
    private static readonly Dictionary<string, string> NotFromTheWindow = new()
    {
        ["LaunchOnLogin"] =
            "Start with Windows applies from its own switch the moment it is flipped, and a flip is refused while a Save runs; " +
            "the Save stores the Windows observation it read before the wait (StartupPreference.Observed(observedStartup, ...)).",
    };

    // Writable settings a Save never assigns: bookkeeping no editor in Settings sets, with the reason.
    private static readonly Dictionary<string, string> NotStoredBySave = new()
    {
        ["HasCompletedFirstRun"] = "the first-run welcome's one-time flag, set by the welcome, never by an editor in Settings",
        ["HasRetiredSeedVocabulary"] = "startup migration bookkeeping (SeedVocabularyRetirement), never shown in Settings",
        ["HasResetFoundryDemotions"] = "startup migration bookkeeping (FoundryDemotionReset), never shown in Settings",
    };

    private const string Subscription =
        "AzureSubscriptionSelection.ResolveAuthenticationSubscription(_selectedAzureDeployment, SelectedAzureSubscription, AzureEndpointBox.Text, AzureDeploymentBox.Text)";

    [Fact]
    public void Every_value_the_save_stores_is_read_by_its_draft()
    {
        // Grok's G2 guard. Every _settings assignment in TrySaveAsync, and every other value it hands SaveBundle, is carried
        // by the draft: an editor its walk reads, or the save's own expression read by the draft, or an explicit exclusion.
        var (window, save, draft, skipped, xaml) = Sources();
        var assignments = Assignments(save);
        Assert.NotEmpty(assignments);
        var definitions = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (property, value) in assignments)
        {
            var expanded = Expand(value, save, window, depth: 0, definitions);
            if (Computed.TryGetValue(property, out var expression))
            {
                Assert.True(Squash(expanded).Contains(Squash(expression), StringComparison.Ordinal), $"The Save no longer stores {property} from {expression}.");
                Assert.True(Squash(draft).Contains(Squash(expression), StringComparison.Ordinal), $"The draft does not read {expression}, which the Save stores as {property}.");
                continue;
            }

            if (NotFromTheWindow.ContainsKey(property))
            {
                continue;
            }

            // Stored straight from editors: every editor it reads is one the walk reads, and it reads nothing else that could
            // change (no field but the stored settings themselves).
            var controls = Regex.Matches(expanded, @"\b[A-Z]\w*\b").Select(match => match.Value).Where(xaml.ContainsKey).Distinct().ToList();
            var fields = Regex.Matches(expanded, @"(?<![\w.])_\w+").Select(match => match.Value).Where(field => field != "_settings").Distinct().ToList();
            Assert.True(controls.Count > 0, $"The Save stores {property} from no editor the draft could read: {value.Trim()}");
            Assert.True(fields.Count == 0, $"The Save stores {property} from {string.Join(", ", fields)}, which the draft does not read.");
            foreach (var control in controls)
            {
                AssertWalked(control, skipped, xaml, $"The Save stores {property} from {control}");
            }
        }

        // No stale entry: each computed value and exclusion is still something the Save stores.
        Assert.All(Computed.Keys.Concat(NotFromTheWindow.Keys), property => Assert.Contains(property, assignments.Keys));

        // The microphone goes in through ApplyTo, not an assignment, and the draft reads the same choice.
        Assert.Contains("_externalMicrophone.ForSave(ShownMicrophone).ApplyTo(_settings);", save, StringComparison.Ordinal);
        Assert.Contains("_externalMicrophone.ForSave(ShownMicrophone)", draft, StringComparison.Ordinal);

        // SaveBundle takes the settings, the dictionary rows, the snippets and the tray intents, and nothing else. The rows
        // are carried by whether they differ from storage (and a row edit in progress); the intents' revisions only order
        // this Save against the tray's own writes, which store themselves, and a tray change that alters what this window
        // would store alters ForSave, which the draft reads.
        var bundle = Regex.Match(save, @"_settingsRepository\.SaveBundle\((?<arguments>[^;]*)\);");
        Assert.True(bundle.Success, "SaveBundle's call was not found.");
        var arguments = Regex.Split(bundle.Groups["arguments"].Value, @",\s*(?![^()]*\))").Select(argument => argument.Trim()).ToList();
        Assert.Equal(4, arguments.Count);
        Assert.Equal("_settings", arguments[0]);
        Assert.Equal("entries", arguments[1]);
        Assert.Equal("snippets", arguments[2]);
        Assert.StartsWith("new ExternalIntents(", arguments[3], StringComparison.Ordinal);
        Assert.Contains("_dictionaryLoad.HasChanges(DictionarySignature())", draft, StringComparison.Ordinal);
        Assert.Contains("RowEditInProgress(DictionaryGrid)", draft, StringComparison.Ordinal);
        Assert.Contains("_snippetLoad.HasChanges(SnippetSignature())", draft, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_writable_setting_is_stored_by_the_save_or_excluded_with_its_reason()
    {
        // Astra's table: a new AppSettings property fails here until it is either stored by the Save (and so carried by the
        // draft, above) or listed as bookkeeping the Save never assigns.
        var (_, save, _, _, _) = Sources();
        var writable = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var stored = Assignments(save).Keys.Concat(["InputDeviceId", "InputDeviceName"]).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(stored.Intersect(NotStoredBySave.Keys));
        Assert.Equal(writable.Order(StringComparer.Ordinal), stored.Concat(NotStoredBySave.Keys).Order(StringComparer.Ordinal));
        Assert.All(NotStoredBySave.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void An_editor_that_applies_its_value_on_focus_loss_is_read_by_the_draft_as_typed()
    {
        // A7 and its sweep: an editor whose value reaches what a Save stores only when it loses focus (the Azure model
        // picker, applied by AzureModelBox_LostFocus) must be read as typed, or an edit typed during the wait with focus
        // still there is closed over. So no editor with a focus-loss handler on a walked page is one the walk skips.
        var (window, _, draft, skipped, xaml) = Sources();
        var withHandler = Document().Descendants()
            .Where(element => element.Attribute("LostFocus") is not null || element.Attribute("LostKeyboardFocus") is not null)
            .ToList();
        Assert.All(withHandler, element => Assert.NotNull(element.Attribute(X + "Name")));
        var handled = withHandler
            .Select(element => element.Attribute(X + "Name")!.Value)
            .Concat(Regex.Matches(window, @"\b(?<control>[A-Z]\w*)\.(LostFocus|LostKeyboardFocus)\s*\+=").Select(match => match.Groups["control"].Value))
            .Distinct()
            .ToList();
        Assert.Contains("AzureModelBox", handled);
        foreach (var control in handled.Where(control => OnWalkedPage(xaml[control])))
        {
            AssertWalked(control, skipped, xaml, $"{control} applies its value on focus loss");
        }

        // The walk reads such an editor by what is typed in it: an editable combo box by its text, a number box by its text
        // as well as its committed value.
        var editors = Body(window, "private static void AppendEditorValues(");
        Assert.Contains("case ComboBox { IsEditable: true } editable:", editors, StringComparison.Ordinal);
        Assert.Contains(".Text(editable.Text)", editors, StringComparison.Ordinal);
        Assert.Contains(".Text(number.Text).Number(number.Value)", editors, StringComparison.Ordinal);
        Assert.True(xaml["AzureModelBox"].Attribute("IsEditable")?.Value == "True", "The Azure model picker is no longer an editable combo box.");

        // A hotkey capture applies only when its keys are released: one in progress shows in the hotkey boxes, which the walk
        // reads, and in the capture flag. Both exist in every version of the window (a capture's own fields do not).
        Assert.DoesNotContain("HotkeyBox", skipped);
        Assert.DoesNotContain("DictationOnlyHotkeyBox", skipped);
        AssertWalked("HotkeyBox", skipped, xaml, "A capture in progress shows in HotkeyBox");
        AssertWalked("DictationOnlyHotkeyBox", skipped, xaml, "A capture in progress shows in DictationOnlyHotkeyBox");
        Assert.Contains(".Flag(_capturing)", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("_capturedKeys", draft, StringComparison.Ordinal);
    }

    [Fact]
    public void The_draft_writes_every_value_it_reads_through_the_framed_snapshot()
    {
        // A8: a value joined into text with a delimiter can make two different drafts hash alike. Everything the draft and its
        // walk read goes through DraftSnapshot, which frames each value; nothing is joined, concatenated or interpolated.
        var (window, _, draft, _, _) = Sources();
        var editors = Body(window, "private static void AppendEditorValues(");
        foreach (var body in new[] { draft, editors })
        {
            Assert.DoesNotContain("StringBuilder", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".Append(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("string.Join", body, StringComparison.Ordinal);
            Assert.DoesNotContain("string.Concat", body, StringComparison.Ordinal);
            Assert.DoesNotContain("$\"", body, StringComparison.Ordinal);
        }

        Assert.Contains("var draft = new Scribe.Core.Vocabulary.DraftSnapshot();", draft, StringComparison.Ordinal);
        Assert.Contains("return draft.Hash();", draft, StringComparison.Ordinal);
        Assert.Contains("Scribe.Core.Vocabulary.DraftSnapshot draft)", editors, StringComparison.Ordinal);
        foreach (var component in new[] { ".Binding(", ".Microphone(", ".Subscription(", ".Profiles(BuildProfiles())", ".LibrarySet(" })
        {
            Assert.Contains(component, draft, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_signature_the_save_and_its_draft_compare_frames_its_values()
    {
        // G3: a Save skips a section whose rows sign like the saved ones, and its draft reads whether they do, so a signature
        // that joins user text lets a changed row sign like the saved one. Every signature in the window frames its values
        // through DraftSnapshot, and every snapshot an editable section is given, and every comparison with it, is that
        // section's own signature, so no two formats ever meet.
        var (window, save, _, _, _) = Sources();
        var signatures = Regex.Matches(window, @"private string (?<name>\w+Signature)\(\)").Select(match => match.Groups["name"].Value).ToList();
        Assert.Equal(["DictionarySignature", "LibrarySignature", "SaveDraftSignature", "SnippetSignature"], signatures.Order(StringComparer.Ordinal));
        foreach (var name in signatures)
        {
            var expression = Regex.Match(window, $@"private string {name}\(\)\s*=>\s*(?<body>[^;]+);");
            var body = expression.Success ? expression.Groups["body"].Value : Body(window, $"private string {name}()");
            Assert.Contains("new Scribe.Core.Vocabulary.DraftSnapshot()", body, StringComparison.Ordinal);
            Assert.Contains(".Hash()", body, StringComparison.Ordinal);
            AssertNoJoining(body, name);
        }

        // Each section signs the rows its SaveAll writes, mapped as the Save's own builders take them.
        Assert.Contains(
            ".DictionaryRows([.. _rows.Select(r => new DictionaryEntryBuilder.Row(r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled))])",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            ".SnippetRows([.. _snippetRows.Select(r => new SnippetBuilder.Row(r.Id, r.Phrase, r.Template, r.Enabled))])",
            window,
            StringComparison.Ordinal);
        Assert.Contains(".LibraryRows([.. _libraryRows.Select(r => (r.Id, r.Enabled))])", window, StringComparison.Ordinal);

        var own = new Dictionary<string, string[]>
        {
            ["_dictionaryLoad"] = ["DictionarySignature()", "dictionarySignature"],
            ["_snippetLoad"] = ["SnippetSignature()", "snippetSignature"],
            ["_libraryLoad"] = ["LibrarySignature()"],
        };
        var calls = 0;
        foreach (Match call in Regex.Matches(window, @"(?<load>_(dictionary|snippet|library)Load)\.(?<method>HasChanges|MarkSaved|TryBegin|Publish)\("))
        {
            var arguments = SplitArguments(Arguments(window, call.Index + call.Length - 1));
            var signature = call.Groups["method"].Value == "Publish" ? arguments[1] : arguments[0];
            Assert.True(
                own[call.Groups["load"].Value].Contains(signature),
                $"{call.Groups["load"].Value}.{call.Groups["method"].Value} is given {signature}, not its section's own signature.");
            calls++;
        }

        Assert.True(calls >= 12, $"Only {calls} section signature calls were found.");
        Assert.Contains("var dictionarySignature = DictionarySignature();", save, StringComparison.Ordinal);
        Assert.Contains("var snippetSignature = SnippetSignature();", save, StringComparison.Ordinal);
    }

    private static void AssertNoJoining(string body, string name)
    {
        foreach (var joining in new[] { "string.Join", "string.Concat", "$\"", "StringBuilder", ".Append(", "AppendJoin", "string.Format" })
        {
            Assert.False(body.Contains(joining, StringComparison.Ordinal), $"{name} joins text with {joining}.");
        }

        Assert.False(Regex.IsMatch(body, @"""\s*\+|\+\s*"""), $"{name} concatenates text with +.");
    }

    // The text between the parenthesis at open and the one that closes it.
    private static string Arguments(string source, int open)
    {
        Assert.Equal('(', source[open]);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '(' or '[' => 1, ')' or ']' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[(open + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced parentheses.");
    }

    // Top-level arguments, split at commas outside any parentheses or brackets.
    private static List<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            depth += arguments[i] switch { '(' or '[' => 1, ')' or ']' => -1, _ => 0 };
            if (arguments[i] == ',' && depth == 0)
            {
                parts.Add(arguments[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(arguments[start..].Trim());
        return parts;
    }

    private static void AssertWalked(string control, IReadOnlyCollection<string> skipped, IReadOnlyDictionary<string, XElement> xaml, string context)
    {
        Assert.True(xaml.ContainsKey(control), $"{context}, which the window does not declare.");
        Assert.DoesNotContain(control, skipped);
        var element = xaml[control];
        Assert.True(OnWalkedPage(element), $"{context}, which is on no page the draft walks.");
        var kinds = element.DescendantsAndSelf().Select(node => node.Name.LocalName).ToList();
        Assert.True(kinds.Any(WalkedKinds.Contains), $"{context}, which is no editor the walk reads.");
    }

    // On a walked page, and outside anything that is not a logical child (templates, resources, tooltips, menus, popups).
    private static bool OnWalkedPage(XElement element)
    {
        var ancestors = element.Ancestors().ToList();
        var page = ancestors.FirstOrDefault(ancestor => WalkedPages.Contains(ancestor.Attribute(X + "Name")?.Value));
        if (page is null)
        {
            return false;
        }

        return !ancestors.TakeWhile(ancestor => ancestor != page).Any(ancestor =>
            ancestor.Name.LocalName.Contains("Template", StringComparison.Ordinal) ||
            ancestor.Name.LocalName.Contains("Resources", StringComparison.Ordinal) ||
            ancestor.Name.LocalName.Contains("ToolTip", StringComparison.Ordinal) ||
            ancestor.Name.LocalName.Contains("ContextMenu", StringComparison.Ordinal) ||
            ancestor.Name.LocalName.Contains("Flyout", StringComparison.Ordinal) ||
            ancestor.Name.LocalName.Contains("Popup", StringComparison.Ordinal));
    }

    // Each property TrySaveAsync assigns on _settings, with the expression it assigns.
    private static Dictionary<string, string> Assignments(string save)
    {
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(save, @"(?<![\w.])_settings\.(?<property>\w+)\s*=(?![=>])\s*(?<value>[^;]+);"))
        {
            var property = match.Groups["property"].Value;
            Assert.False(assignments.ContainsKey(property), $"TrySaveAsync assigns {property} twice.");
            assignments.Add(property, match.Groups["value"].Value);
        }

        return assignments;
    }

    // The expression with every local of TrySaveAsync and every helper property of the window it names written out, so the
    // editors and fields it reads in the end can be seen.
    private static string Expand(string expression, string save, string window, int depth, Dictionary<string, string?> definitions)
    {
        var expanded = expression;
        if (depth > 4)
        {
            return expanded;
        }

        foreach (var name in Regex.Matches(expression, @"\b[A-Za-z]\w*\b").Select(match => match.Value).Distinct())
        {
            if (!definitions.TryGetValue(name, out var definition))
            {
                var local = Regex.Match(save, $@"\bvar\s+{name}\s*=\s*(?<value>[^;]+);");
                var property = Regex.Match(window, $@"private\s+[\w.<>?]+\s+{name}\s*=>\s*(?<value>[^;]+);");
                var block = Regex.Match(window, $@"private\s+[\w.<>?]+\s+{name}\s*\r?\n\s*\{{(?<value>.*?)\r?\n    \}}", RegexOptions.Singleline);
                definition = (local.Success ? local : property.Success ? property : block.Success ? block : null)?.Groups["value"].Value;
                definitions[name] = definition;
            }

            if (definition is not null)
            {
                expanded += " " + Expand(definition, save, window, depth + 1, definitions);
            }
        }

        return expanded;
    }

    private static string Squash(string code) => Regex.Replace(code, @"\s+", string.Empty);

    private static (string Window, string Save, string Draft, IReadOnlyCollection<string> Skipped, IReadOnlyDictionary<string, XElement> Xaml) Sources()
    {
        var window = File.ReadAllText(Path.Combine(Root(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var save = Body(window, "private async Task<bool> TrySaveAsync()");
        var draft = Body(window, "private string SaveDraftSignature()");
        var skippedList = Regex.Match(draft, @"HashSet<DependencyObject> carriedElsewhere =\s*\[(?<names>[^\]]*)\]");
        Assert.True(skippedList.Success, "The draft's list of controls the walk skips was not found.");
        var skipped = skippedList.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var xaml = Document()
            .Descendants()
            .Where(element => element.Attribute(X + "Name") is not null)
            .GroupBy(element => element.Attribute(X + "Name")!.Value)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        return (window, save, draft, skipped, xaml);
    }

    private static XDocument Document() => XDocument.Load(Path.Combine(Root(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));

    // A method's text, from its signature to the closing brace at its indentation.
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Not found: {signature}");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"No end found for: {signature}");
        return source[start..end];
    }

    private static string Root()
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
