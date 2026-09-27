using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The Settings redesign's glossary (one name for each thing), held in the source: no text a person can read in Scribe
/// uses a name the redesign retired, such as "hotkey", "library", "provider" or "post-processing". The test reads every
/// string literal in the app, Core and the overlay, outside comments and outside the text that never reaches a person
/// (log calls and logging helpers, exception messages, regular expressions, nameof, telemetry tags and SQL), and the text
/// attributes and text content of every XAML file. A literal the rules cannot place is still read: the allowlist below
/// names each internal use with its reason, so a new one is a deliberate entry rather than a gap.
/// </summary>
/// <remarks>
/// <para>
/// The list holds the retired words a reader can check mechanically. Words with an ordinary use as well as a retired one
/// (entry, term, enabled, replacement, pattern, template, active, binding, Set, Hold, Toggle) are left to review.
/// </para>
/// <para>
/// What it does not read, so a pass says nothing about it: the docs (README, PRIVACY.md with its defined terms, AGENTS.md,
/// the release notes); the built-in word packs' CSV files, whose text is data; text Scribe builds at run time from data or
/// settings; an exception's message, which the logging rules keep out of the log and the UI never shows; a literal split so
/// a retired word spans two pieces; text a markup extension other than a binding's format and fallbacks produces; and a
/// XAML attribute whose name does not end in a text property's name.
/// </para>
/// </remarks>
public sealed partial class GlossarySourceTests
{
    private static readonly string SourceRoot = Path.Combine(FindRoot(), "src");

    private static readonly string[] Projects = ["Scribe.App", "Scribe.Core", "Scribe.Overlay"];

    private static readonly RetiredWord[] Retired =
    [
        new("hotkey", @"\b[Hh]otkeys?\b", "shortcut"),
        new("chord", @"\b[Cc]hords?\b", "shortcut, or two keys"),
        new("dictation only", @"\b[Dd]ictation[- ]only\b", "shortcut without AI cleanup"),
        new("toggle mode", @"\b[Tt]oggle mode\b", "press to start and stop"),
        new("silence auto-stop", @"\b[Ss]ilence auto-stop\b|\b[Ee]nd dictation on silence\b", "Stop when I stop talking"),
        new("overlay", @"\b[Oo]verlays?\b", "recording indicator"),
        new("pill", @"\b[Pp]ills?\b", "recording indicator"),
        new("voice activity detection", @"\b[Vv]oice activity detection\b|\bVAD\b", "Trim silence"),
        new("post-processing", @"\b[Pp]ost-?processing\b", "your dictionary and snippets"),
        new("decode", @"\b[Dd]ecod(?:e|es|ed|ing)\b", "speech recognition"),
        new("transcription", @"\b[Tt]ranscri(?:be|bes|bed|ber|bing|ption|ptions|pt|pts)\b", "speech recognition, or what Scribe heard"),
        new("recognizer", @"\b[Rr]ecogni[sz]ers?\b", "speech model"),
        new("real-time factor", @"\bRTF\b|\b[Rr]eal-time factor\b", "times faster than real time"),
        new("latency", @"\b[Ll]atency\b|\b[Rr]ound trip\b", "time, or how long it took"),
        new("percentile", @"\bP50\b|\bP95\b|\b[Pp]ercentiles?\b", "typical, or 19 in 20 finish within"),
        new("pipeline", @"\b[Pp]ipelines?\b", "each step"),
        new("polish", @"\b[Pp]olish(?:es|ed|ing)?\b", "AI cleanup"),
        new("Intelligence", @"\bIntelligence\b", "AI cleanup"),
        new("provider", @"\b[Pp]roviders?\b", "where AI cleanup runs, or AI service"),
        new("on-device", @"\b[Oo]n-device\b", "on this PC"),
        new("endpoint", @"\bAI endpoints?\b|\b[Cc]ustom endpoints?\b|OpenAI-compatible endpoints?|\b[Ee]ndpoint URLs?\b|\b[Bb]ase URLs?\b", "another AI service, or server address"),
        new("az login", @"\baz login\b", "Azure CLI sign-in"),
        new("CLI", @"\bCLI\b", "command-line tool, after the product name"),
        new("licence", @"\b[Ll]icen[cs]es?\b", "subscription"),
        new("execution provider", @"\b[Ee]xecution providers?\b|\b[Hh]ardware runtimes?\b", "AI runtime for this PC"),
        new("DPAPI", @"\bDPAPI\b", "saved encrypted on this PC with your Windows account"),
        new("prompt", @"\b(?:[Ff]rontier|[Ll]ocal|[Ss]ystem|[Cc]leanup|[Ss]tyle) prompts?\b|\b[Pp]rompt styles?\b", "detailed instructions, or short instructions"),
        new("raw text", @"\b[Rr]aw (?:text|transcripts?)\b", "what Scribe heard"),
        new("glossary", @"\b[Gg]lossar(?:y|ies)\b", "vocabulary"),
        new("library", @"\b[Ll]ibrar(?:y|ies)\b", "word pack"),
        new("trigger phrase", @"\b[Tt]rigger phrases?\b|\b[Ee]xpands to\b", "When you say, and Scribe types"),
        new("process name", @"\b[Pp]rocess names?\b", "apps"),
        new("playground", @"\b[Pp]layground\b|\b[Tt]est box\b", "Try dictation"),
        new("text insertion", @"\b[Tt]ext insertion\b|\b[Ii]njection\b", "typing into apps"),
        new("insertion choice", @"\bType it in\b|\bPaste it in\b", "Type the text, or Paste the text"),
        new("terminal", @"\bterminals?\b|\bTerminals\b|\b[Ss]hells?\b", "command windows, such as Terminal"),
        new("insight", @"\b[Ii]nsights?\b", "AI summary"),
        new("log bundle", @"\b[Ll]og bundles?\b", "diagnostics"),
        new("Settings saved", @"\bSettings saved\b", "Changes saved"),
        new("Forever", @"\bForever\b", "Until I delete them"),
        new("No unsaved changes", @"\bNo unsaved changes\b", "All changes saved"),
    ];

    // Other companies' product names, which keep their own words.
    private static readonly Regex[] ProductNames = [new(@"\bAzure CLI\b"), new(@"\bMIT License\b")];

    // Files whose text is never Scribe's words to a person, with why. Paths are relative to src.
    private static readonly IReadOnlyDictionary<string, string> ExcludedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [@"Scribe.Core\Cleanup\CleanupPrompt.cs"] =
            "The instructions and vocabulary format the AI model is given, benchmark-validated (docs/model-leaderboard.md). " +
            "Settings shows the defaults for editing exactly as the model reads them.",
        [@"Scribe.Core\Settings\SettingsSearchIndex.cs"] =
            "Find a setting's keywords keep the old names on purpose, so \"hotkey\" still finds the shortcut. Its labels " +
            "must equal the page's (SettingsSearchIndexTests), and this test reads the page itself.",
        [@"Scribe.Core\Diagnostics\SessionBanner.cs"] = "Writes the log's session banner.",
        [@"Scribe.Core\Diagnostics\HistoricalLogRedaction.cs"] = "Matches what earlier builds wrote to the log.",
        [@"Scribe.Core\Diagnostics\LogLineRedactor.cs"] = "Matches what earlier builds wrote to the log.",
        [@"Scribe.Core\Diagnostics\DiagnosticsBundle.cs"] = "Writes report.txt in the diagnostics zip, for whoever investigates a report.",
        [@"Scribe.Core\Diagnostics\TraceTagPolicy.cs"] = "The trace tag policy.",
        [@"Scribe.Core\Feedback\AiContentReport.cs"] = "The AI result report a person sends to the maintainer.",
        [@"Scribe.App\Infrastructure\FileLoggerProvider.cs"] = "The log writer.",
        [@"Scribe.Overlay\Logging\OverlayLog.cs"] = "The overlay's log writer.",
    };

    // Members whose initializer is a request to the AI model, with why.
    private static readonly (string File, string Member, string Why)[] ExcludedMembers =
    [
        (@"Scribe.Core\Diagnostics\UsageInsight.cs", "SystemPrompt", "The request the AI model is given for the AI summary."),
        (@"Scribe.Core\PostProcessing\AiDictionarySuggester.cs", "SystemPrompt", "The request the AI model is given for Learn from history."),
    ];

    // Literals that hold a retired word on purpose and that no rule places, with why. Compared as trimmed text.
    private static readonly (string File, string Text, string Why)[] Allowed =
    [
        (@"Scribe.App\Dictation\DictationController.cs", "stop the hotkey hook", "A shutdown step's name, which the log shows."),
        (@"Scribe.App\Overlay\OverlayProcessClient.cs", "Overlay", "The installer layout's folder that holds Scribe.Overlay.exe."),
        (@"Scribe.Core\Cleanup\TextCleanupService.cs", "compute pipeline", "Words of the WebGPU error the Foundry Local SDK raises, matched to recognize it."),
        (@"Scribe.Core\Cleanup\TextCleanupService.cs", "execution provider, which is not available", "Words of the Foundry Local SDK's own error, matched to recognize it."),
        (@"Scribe.Core\Cleanup\VocabularyHandOff.cs",
            "The request was not handed over: the library vocabulary it was admitted with is no longer permitted.",
            "An exception's message, which nothing shows or logs."),
        (@"Scribe.Core\Libraries\LibraryComposition.cs", "(transcribed as \"", "The vocabulary line format the AI model is given."),
        (@"Scribe.Core\Libraries\LibraryManifest.cs",
            "A library change holds text that is not well-formed UTF-16 (an unpaired surrogate).", "An exception's message."),
        (@"Scribe.Core\Persistence\StorageMaintenance.cs", "library retention", "A maintenance step's name, which the log shows."),
        (@"Scribe.Core\PostProcessing\DictionaryLibraryService.cs", "A library change is still being saved.", "An exception's message."),
        (@"Scribe.Core\Settings\ProfilePresets.cs", "Terminals and shells",
            "A former preset name, kept so a profile added under it still counts as the preset (Preset.IsNamed)."),
        (@"Scribe.Core\Settings\ProfilePresets.cs", "IDE integrated terminals",
            "A former preset name, kept so a profile added under it still counts as the preset (Preset.IsNamed)."),
        (@"Scribe.Core\Settings\TryDictationRules.cs", "Voice activity detection",
            "A Try dictation stage key the controller reports and StageFrom maps; the page shows UserFacingError.StageName."),
        (@"Scribe.Core\Settings\TryDictationRules.cs", "Text insertion",
            "A Try dictation stage key the controller reports and StageFrom maps; the page shows UserFacingError.StageName."),
    ];

    // The calls whose text never reaches a person. Matched on code with literals and comments blanked out.
    [GeneratedRegex(
        @"\.\s*Log(?:Trace|Debug|Information|Warning|Error|Critical)?\s*\(" +
        @"|(?<![\w.])(?:TryLog\w*|Log[A-Z]\w*)\s*\(" +
        @"|(?<![\w.])(?:OverlayLog|Debug|Trace|Console)\s*\.\s*\w+\s*\(" +
        @"|(?<![\w.])nameof\s*\(" +
        @"|\.\s*(?:SetTag|AddTag|SetStatus|AddEvent)\s*\(" +
        @"|\bnew\s+(?:[\w.]+\.)?\w*Exception\s*\(" +
        @"|\[\s*GeneratedRegex\s*\(" +
        @"|\bnew\s+Regex\s*\(" +
        @"|(?<![\w.])Regex\s*\.\s*\w+\s*\(" +
        @"|\bWriteStep\s*\?\s*\.\s*Invoke\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex HiddenCall();

    // A statement of SQL, which the database reads.
    [GeneratedRegex(@"^\s*(?:SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|PRAGMA|WITH|BEGIN)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Sql();

    // Any attribute whose name ends in a text property (Text, PlaceholderText, HelpText, Content, Header, ToolTip and the
    // like, a custom control's included), and an element's accessible name, in either quote style.
    [GeneratedRegex(
        @"(?<![\w.:])(?:\w+:)?(?:AutomationProperties\.(?:Name|ItemStatus)|(?:\w+\.)?\w*(?:" + TextPropertyNames + @"))" +
        @"\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
        RegexOptions.CultureInvariant)]
    private static partial Regex XamlTextAttribute();

    private const string TextPropertyNames =
        "Text|Content|Title|Message|ToolTip|Header|Description|Label|Subtitle|Caption|StringFormat|TargetNullValue|FallbackValue";

    // The texts a binding shows: its format and its fallbacks, quoted or not.
    [GeneratedRegex(@"\b(?:StringFormat|TargetNullValue|FallbackValue)\s*=\s*(?:'(?<text>[^']*)'|(?<text>\{\}.*?)(?=,\s*\w+\s*=|\}\s*$)|(?<text>[^,{}]*))", RegexOptions.CultureInvariant)]
    private static partial Regex XamlBindingText();

    [GeneratedRegex(@"<Setter\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex XamlSetter();

    [GeneratedRegex(@"^(?:\w+:)?(?:AutomationProperties\.(?:Name|HelpText|ItemStatus)|(?:\w+\.)?\w*(?:" + TextPropertyNames + @"))$", RegexOptions.CultureInvariant)]
    private static partial Regex XamlTextProperty();

    // Every text node, so mixed content (<TextBlock>Press <Run .../> to start</TextBlock>) is read in pieces.
    [GeneratedRegex(@">(?<text>[^<>]*\p{L}[^<>]*)<", RegexOptions.CultureInvariant)]
    private static partial Regex XamlTextContent();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex XamlComment();

    [Fact]
    public void No_text_a_person_reads_uses_a_word_the_glossary_retired()
    {
        var offences = new List<string>();
        foreach (var item in UserFacingText())
        {
            if (IsAllowed(item.File, item.Text))
            {
                continue;
            }

            offences.AddRange(RetiredIn(item.Text).Select(word =>
                $"{item.File}:{item.Line}: \"{word.Name}\" (say {word.Say}): {Shorten(item.Text)}"));
        }

        Assert.True(
            offences.Count == 0,
            "Text a person reads uses a word the glossary retired. Reword it, or, if no person reads it, add a rule or an " +
            "allowlist entry with the reason:\n" + string.Join('\n', offences));
    }

    [Fact]
    public void Every_exception_to_the_glossary_is_still_needed()
    {
        var holding = UserFacingText()
            .Where(item => RetiredIn(item.Text).Any())
            .Select(item => (item.File, item.Text.Trim()))
            .ToHashSet();

        foreach (var (file, text, why) in Allowed)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            Assert.True(holding.Contains((file, text.Trim())), $"No longer needed, remove it from Allowed: {file}: {text}");
        }

        foreach (var (file, why) in ExcludedFiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            Assert.True(File.Exists(Path.Combine(SourceRoot, file)), $"No longer exists, remove it from ExcludedFiles: {file}");
        }

        foreach (var (file, member, why) in ExcludedMembers)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            var source = File.ReadAllText(Path.Combine(SourceRoot, file));
            Assert.NotEmpty(SourceLiterals.MemberInitializers(source, member));
        }
    }

    [Fact]
    public void The_reader_finds_the_text_a_person_can_see_and_nothing_else()
    {
        const string source = """"
            // Your hotkey in a comment
            /* Your hotkey in a block comment */
            /// <summary>Your hotkey in a doc comment.</summary>
            #region Don't let a directive's apostrophe start a character
            partial class Sample
            {
                void Run(ILogger log, Exception ex, int n)
                {
                    log.LogWarning("Your hotkey {N} in a log call", n);
                    TryLog(ex, "Your hotkey in a logging helper");
                    LogProviderFailure(LogLevel.Debug, ex, "Your hotkey in another logging helper");
                    throw new InvalidOperationException("Your hotkey in an exception");
                }

                [GeneratedRegex(@"Your hotkey in a pattern")] private static partial Regex Pattern();
                string A => $"{hotkeyName} is only in a hole";
                string B => $"Change your hotkey {n:N0} times";
                string C => @"Your ""hotkey"" in a verbatim string";
                char Q => '"';
                string D => "Your hotkey after a quote character";
                string E => """
                    Your hotkey in a raw string, "quoted" inside
                    """;
                string F => $$"""{{hotkeyHole}} and {literal braces}""";
                string G => "hotkeys";
                string I => $"Status: {(ready ? "Your hotkey is ready" : "Not ready")}";
                string J => $$"""Name: {{$"{hotkeyName}"}} and {{Describe(hotkey)}}""";
                void K(ILogger log) => log.LogWarning($"Held {(held ? "Your hotkey in a log call's hole" : "no")}");
                string H => "SELECT hotkey FROM settings";
                public const string SystemPrompt = "Your hotkey in a request to the model";
            }
            #endregion
            """";

        var found = SourceLiterals.Read(source, SourceLiterals.MemberInitializers(source, "SystemPrompt"))
            .Where(literal => !IsKeyOrSql(literal.Text) && literal.Text.Contains("hotkey", StringComparison.Ordinal))
            .Select(literal => literal.Text)
            .ToList();

        Assert.Equal(5, found.Count);
        Assert.Contains(found, text => text.StartsWith("Change your hotkey ", StringComparison.Ordinal) && text.EndsWith(" times", StringComparison.Ordinal));
        Assert.Contains("Your \"hotkey\" in a verbatim string", found);
        Assert.Contains("Your hotkey after a quote character", found);
        Assert.Contains(found, text => text.Contains("Your hotkey in a raw string, \"quoted\" inside", StringComparison.Ordinal));

        // A literal inside a hole is shown when the hole renders it; code inside a raw string's hole is not text.
        Assert.Contains("Your hotkey is ready", found);
        Assert.DoesNotContain(found, text => text.StartsWith("Name:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_reader_finds_xaml_text_attributes_and_content_and_nothing_else()
    {
        const string xaml = """
            <StackPanel>
                <!-- Your hotkey in a comment -->
                <TextBlock Text="Your hotkey in text" TextWrapping="Wrap"/>
                <ui:Button Content="{Binding Hotkey}" ToolTip="Your hotkey in a tooltip" Tag="hotkey"/>
                <TextBlock>Your hotkey as content</TextBlock>
                <TextBlock AutomationProperties.HelpText="Your hotkey &amp; more"/>
                <TextBlock Text='Your hotkey in single quotes'/>
                <Setter Property="ToolTip" Value="Your hotkey in a setter"/>
                <Setter Property="Background" Value="HotkeyBrush"/>
                <TextBlock Text="{Binding Count, StringFormat='{}{0} hotkey presses'}"/>
                <settings:StatusRow PrimaryActionText="Your hotkey action" Tag="{Binding Hotkey}"/>
                <TextBlock>Press <Run Text="{Binding Key}"/> to change your hotkey</TextBlock>
            </StackPanel>
            """;

        var found = ReadXaml(xaml).Select(item => item.Text.Trim()).ToList();

        Assert.Equal(
            [
                "Your hotkey in text", "Your hotkey in a tooltip", "Your hotkey as content", "Your hotkey & more",
                "Your hotkey in single quotes", "Your hotkey in a setter", "hotkey presses", "Your hotkey action",
                "Press", "to change your hotkey",
            ],
            found);
    }

    [Fact]
    public void The_retired_words_leave_their_replacements_and_product_names_alone()
    {
        foreach (var text in new[]
                 {
                     "Choose Change next to your shortcut.", "Your Azure account (Azure CLI) (recommended)",
                     "Scribe is open source under the MIT License.", "In command windows such as Terminal, a line break works like Enter.",
                     "Word packs", "Where AI cleanup runs", "Another AI service", "Server address", "Speech recognition",
                     "Stop when I stop talking", "Show the recording indicator", "Changes saved.",
                 })
        {
            Assert.Empty(RetiredIn(text));
        }

        foreach (var (text, word) in new[]
                 {
                     ("Consider a two-key chord instead.", "chord"), ("Your AI provider receives", "provider"),
                     ("Couldn't reach the AI endpoint.", "endpoint"), ("The GitHub Copilot CLI is not installed.", "CLI"),
                     ("This dictation used raw text.", "raw text"), ("Terminals and shells", "terminal"),
                     ("Settings saved.", "Settings saved"), ("The on-device model", "on-device"),
                 })
        {
            Assert.Contains(RetiredIn(text), retired => retired.Name == word);
        }
    }

    private static IEnumerable<(string File, int Line, string Text)> UserFacingText()
    {
        foreach (var project in Projects)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(SourceRoot, project), "*.*", SearchOption.AllDirectories))
            {
                var file = Path.GetRelativePath(SourceRoot, path).Replace('/', '\\');
                if (file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                    ExcludedFiles.ContainsKey(file))
                {
                    continue;
                }

                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    // An exception type's file holds its messages, which nothing shows.
                    if (path.EndsWith("Exception.cs", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var source = File.ReadAllText(path);
                    var excluded = ExcludedMembers
                        .Where(member => string.Equals(member.File, file, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(member => SourceLiterals.MemberInitializers(source, member.Member))
                        .ToList();
                    foreach (var literal in SourceLiterals.Read(source, excluded))
                    {
                        if (!IsKeyOrSql(literal.Text))
                        {
                            yield return (file, literal.Line, literal.Text);
                        }
                    }
                }
                else if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (line, text) in ReadXaml(File.ReadAllText(path)))
                    {
                        yield return (file, line, text);
                    }
                }
            }
        }
    }

    // A single token with no letter in upper case, or with the punctuation of a key, a path, a code or a markup tag, is a
    // key ("hotkeys", "libraries.state", "execution-provider-unavailable", "Scribe.Overlay.exe", "<transcript>"); SQL is
    // read by the database.
    private static bool IsKeyOrSql(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || Sql().IsMatch(trimmed))
        {
            return true;
        }

        return !trimmed.Any(char.IsWhiteSpace) &&
               (!trimmed.Any(char.IsUpper) || trimmed.IndexOfAny(['.', '_', '-', '<', '>', '/', '\\', '$', ':', '@', '=', '#']) >= 0);
    }

    private static IEnumerable<RetiredWord> RetiredIn(string text)
    {
        var withoutProducts = ProductNames.Aggregate(text, (current, product) => product.Replace(current, " "));
        return Retired.Where(word => word.Regex.IsMatch(withoutProducts));
    }

    private static bool IsAllowed(string file, string text) =>
        Allowed.Any(entry => string.Equals(entry.File, file, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(entry.Text.Trim(), text.Trim(), StringComparison.Ordinal));

    private static IEnumerable<(int Line, string Text)> ReadXaml(string xaml)
    {
        // Comments blanked out with their line breaks kept, so line numbers stay true.
        var text = XamlComment().Replace(xaml, match => Regex.Replace(match.Value, @"[^\n]", " "));
        var found = new List<(int Index, string Text)>();
        foreach (Match match in XamlTextAttribute().Matches(text))
        {
            found.AddRange(XamlValueTexts(match.Groups["value"].Value).Select(value => (match.Index, value)));
        }

        // A style's setter for a text property shows its value too.
        foreach (Match setter in XamlSetter().Matches(text))
        {
            var property = XamlAttribute(setter.Groups["attrs"].Value, "Property");
            var value = XamlAttribute(setter.Groups["attrs"].Value, "Value");
            if (property is not null && value is not null && XamlTextProperty().IsMatch(property))
            {
                found.AddRange(XamlValueTexts(value).Select(shown => (setter.Index, shown)));
            }
        }

        foreach (Match match in XamlTextContent().Matches(text))
        {
            found.Add((match.Index, WebUtility.HtmlDecode(match.Groups["text"].Value.Trim())));
        }

        return found.OrderBy(item => item.Index).Select(item => (LineOf(text, item.Index), item.Text));
    }

    // What an attribute value shows: the value itself, or, for a markup extension, which is code, its binding's format and
    // fallback texts. Format placeholders ({0}, {0:N0}) and the {} escape are not text.
    private static IEnumerable<string> XamlValueTexts(string value)
    {
        var shown = value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal)
            ? XamlBindingText().Matches(value).Select(part => part.Groups["text"].Value)
            : [value];
        return shown
            .Select(text => Regex.Replace(WebUtility.HtmlDecode(text), @"^\{\}|\{\d+(?:[,:][^}]*)?\}", " "))
            .Where(text => !string.IsNullOrWhiteSpace(text));
    }

    private static string? XamlAttribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"(?<![\w.:]){Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')");
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Shorten(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }

    private static string FindRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private sealed record RetiredWord(string Name, string Pattern, string Say)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Reads C# source's string literals the way the compiler delimits them: regular, verbatim, interpolated and raw
    /// strings, with char literals, comments and preprocessor lines skipped, and with interpolation holes, which are code,
    /// left out of the text. Deliberately small: it understands exactly enough C# to find text, nothing more.
    /// </summary>
    internal static class SourceLiterals
    {
        internal readonly record struct Literal(int Start, int Line, string Text);

        private enum Kind
        {
            Comment,
            Literal,
            Char,
        }

        private readonly record struct Token(Kind Kind, int Start, int End, string Text);

        /// <summary>
        /// The literals outside comments, outside the calls whose text never reaches a person, and outside
        /// <paramref name="excluded"/>.
        /// </summary>
        public static IEnumerable<Literal> Read(string source, IReadOnlyList<(int Start, int End)> excluded)
        {
            var tokens = Tokenize(source);
            var masked = Mask(source, tokens);
            var hidden = new List<(int Start, int End)>(excluded);
            foreach (Match call in HiddenCall().Matches(masked))
            {
                hidden.Add((call.Index, MatchingClose(masked, call.Index + call.Length - 1)));
            }

            foreach (var token in tokens)
            {
                if (token.Kind != Kind.Literal || hidden.Any(span => token.Start >= span.Start && token.Start < span.End))
                {
                    continue;
                }

                yield return new Literal(token.Start, LineOf(source, token.Start), token.Text);
            }
        }

        /// <summary>The spans of <paramref name="member"/>'s initializers: from the name to the end of its statement.</summary>
        public static IReadOnlyList<(int Start, int End)> MemberInitializers(string source, string member)
        {
            var masked = Mask(source, Tokenize(source));
            var spans = new List<(int Start, int End)>();
            foreach (Match match in Regex.Matches(masked, $@"\b{Regex.Escape(member)}\s*(?:=>|=(?!=))"))
            {
                var depth = 0;
                var end = match.Index + match.Length;
                while (end < masked.Length && !(depth == 0 && masked[end] == ';'))
                {
                    depth += masked[end] is '(' or '[' or '{' ? 1 : masked[end] is ')' or ']' or '}' ? -1 : 0;
                    end++;
                }

                spans.Add((match.Index, end));
            }

            return spans;
        }

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var nested = new List<Token>();
                if (TryRead(s, i, out var token, nested))
                {
                    // A literal inside an interpolation hole is text too, when the hole renders it.
                    tokens.Add(token);
                    tokens.AddRange(nested);
                    i = Math.Max(token.End, i + 1);
                }
                else
                {
                    i++;
                }
            }

            return tokens;
        }

        // Literal and comment text blanked out, line breaks kept, so a call's parentheses can be matched on code alone.
        private static string Mask(string source, List<Token> tokens)
        {
            var chars = source.ToCharArray();
            foreach (var token in tokens)
            {
                for (var k = token.Start; k < token.End && k < chars.Length; k++)
                {
                    if (chars[k] != '\n')
                    {
                        chars[k] = ' ';
                    }
                }
            }

            return new string(chars);
        }

        private static int MatchingClose(string masked, int open)
        {
            var depth = 0;
            for (var k = open; k < masked.Length; k++)
            {
                if (masked[k] is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (masked[k] is ')' or ']' or '}' && --depth == 0)
                {
                    return k + 1;
                }
            }

            return masked.Length;
        }

        private static bool TryRead(string s, int i, out Token token, List<Token>? nested = null)
        {
            token = default;
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '*'))
            {
                var end = s[i + 1] == '/' ? s.IndexOf('\n', i) : s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                token = new Token(Kind.Comment, i, end < 0 ? s.Length : s[i + 1] == '/' ? end : end + 2, string.Empty);
                return true;
            }

            // A preprocessor line is not code: "#region Don't" must not start a char literal.
            if (c == '#' && AtLineStart(s, i))
            {
                var end = s.IndexOf('\n', i);
                token = new Token(Kind.Comment, i, end < 0 ? s.Length : end, string.Empty);
                return true;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < s.Length && s[j] != '\'' && s[j] != '\n')
                {
                    j += s[j] == '\\' ? 2 : 1;
                }

                token = new Token(Kind.Char, i, Math.Min(j + 1, s.Length), string.Empty);
                return true;
            }

            if (c is not ('$' or '@' or '"') || (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')))
            {
                return false;
            }

            var start = i;
            var dollars = 0;
            var verbatim = false;
            while (i < s.Length && s[i] is '$' or '@')
            {
                dollars += s[i] == '$' ? 1 : 0;
                verbatim |= s[i] == '@';
                i++;
            }

            if (i >= s.Length || s[i] != '"')
            {
                return false;
            }

            var quotes = 0;
            while (i + quotes < s.Length && s[i + quotes] == '"')
            {
                quotes++;
            }

            if (quotes >= 3 && !verbatim)
            {
                var close = new string('"', quotes);
                var raw = new StringBuilder();
                var r = i + quotes;
                while (r < s.Length && string.CompareOrdinal(s, r, close, 0, quotes) != 0)
                {
                    // A raw string's hole opens with as many braces as it has dollar signs; any braces before those are text.
                    var run = 0;
                    while (dollars > 0 && r + run < s.Length && s[r + run] == '{')
                    {
                        run++;
                    }

                    if (dollars > 0 && run >= dollars)
                    {
                        raw.Append(s, r, run - dollars);
                        r = SkipHole(s, r + run, nested);
                        for (var extra = 1; extra < dollars && r < s.Length && s[r] == '}'; extra++)
                        {
                            r++;
                        }

                        raw.Append(' ');
                        continue;
                    }

                    raw.Append(s[r]);
                    r++;
                }

                token = new Token(Kind.Literal, start, Math.Min(r + quotes, s.Length), raw.ToString());
                return true;
            }

            var text = new StringBuilder();
            var k = i + 1;
            while (k < s.Length)
            {
                var ch = s[k];
                if (dollars > 0 && ch == '{')
                {
                    if (k + 1 < s.Length && s[k + 1] == '{')
                    {
                        text.Append('{');
                        k += 2;
                        continue;
                    }

                    k = SkipHole(s, k + 1, nested);
                    text.Append(' ');
                    continue;
                }

                if (dollars > 0 && ch == '}' && k + 1 < s.Length && s[k + 1] == '}')
                {
                    text.Append('}');
                    k += 2;
                    continue;
                }

                if (verbatim && ch == '"')
                {
                    if (k + 1 < s.Length && s[k + 1] == '"')
                    {
                        text.Append('"');
                        k += 2;
                        continue;
                    }

                    k++;
                    break;
                }

                if (!verbatim && ch == '\\' && k + 1 < s.Length)
                {
                    k = Unescape(s, k, text);
                    continue;
                }

                if (!verbatim && (ch == '"' || ch == '\n'))
                {
                    // A regular literal ends at its closing quote; one left open ends with its line.
                    k += ch == '"' ? 1 : 0;
                    break;
                }

                text.Append(ch);
                k++;
            }

            token = new Token(Kind.Literal, start, k, text.ToString());
            return true;
        }

        // From just inside a hole's brace to just past its closing brace, nested brackets skipped and nested literals
        // collected: a hole such as {(ready ? "Ready" : "Not ready")} renders its literals.
        private static int SkipHole(string s, int k, List<Token>? nested)
        {
            var depth = 0;
            while (k < s.Length)
            {
                if (s[k] is '"' or '\'' or '$' or '@' && TryRead(s, k, out var inner, nested) && inner.Kind != Kind.Comment)
                {
                    if (inner.Kind == Kind.Literal)
                    {
                        nested?.Add(inner);
                    }

                    k = Math.Max(inner.End, k + 1);
                    continue;
                }

                if (s[k] is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (s[k] is ')' or ']')
                {
                    depth--;
                }
                else if (s[k] == '}')
                {
                    if (depth == 0)
                    {
                        return k + 1;
                    }

                    depth--;
                }

                k++;
            }

            return s.Length;
        }

        private static int Unescape(string s, int k, StringBuilder text)
        {
            var escape = s[k + 1];
            if (escape is 'u' && k + 5 < s.Length && int.TryParse(s.AsSpan(k + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                text.Append((char)code);
                return k + 6;
            }

            text.Append(escape switch
            {
                '"' => '"',
                '\\' => '\\',
                '\'' => '\'',
                _ => ' ',
            });
            return k + 2;
        }

        private static bool AtLineStart(string s, int i)
        {
            var k = i - 1;
            while (k >= 0 && s[k] is ' ' or '\t')
            {
                k--;
            }

            return k < 0 || s[k] == '\n';
        }
    }
}
