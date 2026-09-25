using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The dictionary page's status line says what AI cleanup receives, so its number must be the one
/// dictation sends. It used to count only the page's rows and tell a cloud user that "all of them" were
/// sent; then it counted in the grid's order, while dictation reads the saved dictionary by pattern, so
/// after an out-of-order import the size budget stopped at a different line. These tests hold it to the
/// pipeline: the saved dictionary read back by the real repository, the libraries in the real service's
/// order, and the glossary's own composition, budget and selection.
/// </summary>
public sealed class GlossaryHintTests
{
    private static DictionaryEntryBuilder.Row Row(string pattern, string replacement, bool enabled = true) =>
        new(0, pattern, replacement, WholeWord: true, enabled);

    private static DictionaryLibrary Library(string id, params DictionaryEntry[] entries) =>
        new(id, id, "Custom", null, BuiltIn: false, entries);

    private static string Describe(
        IReadOnlyList<DictionaryEntryBuilder.Row> rows,
        IReadOnlyList<DictionaryLibrary> libraries,
        CleanupProvider provider = CleanupProvider.AzureFoundry,
        CleanupPromptStyle style = CleanupPromptStyle.Auto,
        bool aiCleanupOn = true,
        bool postProcessingOn = true) =>
        GlossaryHint.Describe(new GlossaryHint.Input(rows, libraries, aiCleanupOn, postProcessingOn, provider, style));

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    [Fact]
    public void With_ai_cleanup_off_it_only_counts_the_entries()
    {
        // A rule that deletes its phrase is as enabled as one that replaces it, and a blank row is no entry.
        var text = Describe([Row("azure", "Azure"), Row("um", string.Empty), Row(" ", string.Empty)], [], aiCleanupOn: false);

        Assert.Equal("2 of 2 entries enabled.", text);
    }

    [Fact]
    public void With_post_processing_off_it_says_the_dictionary_is_not_applied_here()
    {
        Assert.Equal(
            "1 of 1 entries enabled. Post-processing is off, so it is not applied on this PC.",
            Describe([Row("azure", "Azure")], [], aiCleanupOn: false, postProcessingOn: false));
        Assert.Equal(
            "2 of 2 entries enabled. Post-processing is off, so none of them are applied on this PC.",
            Describe([Row("azure", "Azure"), Row("um", string.Empty)], [], aiCleanupOn: false, postProcessingOn: false));

        // And AI cleanup still receives the vocabulary, as the post-processing switch itself says.
        Assert.Equal(
            "1 of 1 entries enabled. Post-processing is off, so it is not applied on this PC. Your AI " +
            "provider receives that term as vocabulary with every cleanup request, whether or not the dictation " +
            "mentions it.",
            Describe([Row("azure", "Azure")], [], postProcessingOn: false));
    }

    [Fact]
    public void A_remote_provider_is_said_to_receive_every_term_libraries_included_whether_or_not_it_is_said()
    {
        var text = Describe(
            [Row("azure", "Azure"), Row("a p i m", "APIM")],
            [Library("team", DictionaryEntry.New("azure", "AZURE-from-library"), DictionaryEntry.New("cosmos db", "Cosmos DB"), DictionaryEntry.New("k eight s", "K8s"))]);

        Assert.Equal(
            "2 of 2 entries enabled plus 2 from enabled libraries. All of them are applied on this PC. Your AI " +
            "provider receives all 4 terms as vocabulary with every cleanup request, whether or not the dictation " +
            "mentions them.",
            text);
    }

    [Fact]
    public void An_on_device_list_cut_to_the_local_budget_quotes_the_count_the_glossary_is_built_with()
    {
        var rows = Enumerable.Range(0, 150).Select(i => Row($"spoken {i}", $"Term{i}")).ToList();
        var library = Library("big", [.. Enumerable.Range(0, 400).Select(i => DictionaryEntry.New($"library {i}", $"Library{i}"))]);

        var text = Describe(rows, [library], CleanupProvider.FoundryLocal);

        Assert.Contains(
            $"The on-device model receives the first {CleanupPrompt.MaxGlossaryTermsLocal} of 550 terms as vocabulary",
            text, StringComparison.Ordinal);
        Assert.Contains("Your own entries come first.", text, StringComparison.Ordinal);
        Assert.EndsWith(
            $"The Local prompt style stops the list at {CleanupPrompt.MaxGlossaryTermsLocal} terms so it fits a small model's context.",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void After_an_out_of_order_import_the_count_is_the_one_dictation_sends()
    {
        // Imported long-first, while dictation reads the saved dictionary by pattern, short-first. The size
        // budget stops after about 200 long lines in the grid's order but about 800 short ones in dictation's.
        var longEntries = Enumerable.Range(0, 1550).Select(i => Row($"z {i:D4}", new string('L', 90) + i.ToString("D4", CultureInfo.InvariantCulture)));
        var shortEntries = Enumerable.Range(0, 1550).Select(i => Row($"a {i:D4}", $"T{i:D4}"));
        var rows = longEntries.Concat(shortEntries).ToList();

        var text = Describe(rows, []);

        var sent = SentByDictation(rows, []);
        var gridOrder = CleanupPrompt.CountGlossary(DictionaryEntryBuilder.Build(rows).Entries).Included;
        Assert.True(sent > gridOrder * 2, $"The fixture must separate the orders ({sent} against {gridOrder}), or it proves nothing.");
        Assert.Contains($"Your AI provider receives the first {N(sent)} of 3,100 terms", text, StringComparison.Ordinal);
        Assert.EndsWith(
            $"The list stops at {N(CleanupPrompt.MaxGlossaryTermsCloud)} terms or {N(CleanupPrompt.MaxGlossaryChars)} characters.",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_imported_this_session_is_counted_in_the_order_the_service_loads_it()
    {
        // The page appends a library it imports, while the service reads the folder in file-name order.
        // With the size budget binding, the order decides which library's long lines are counted first.
        using var folder = new TempDirectory();
        using var db = ScribeDatabase.CreateInMemory();
        var service = new DictionaryLibraryService(new AppPaths(folder.Combine("data")), new SettingsRepository(db), NullLogger<DictionaryLibraryService>.Instance);
        var zeta = service.Import(Csv("zeta", 400, 95), "zeta");
        var alpha = service.Import(Csv("alpha", 400, 5), "alpha");
        var alpha2 = service.Import(Csv("gamma", 400, 60), "alpha");
        Assert.Equal("alpha-2", alpha2.Id);

        var inPageOrder = new[] { zeta, alpha2, alpha };
        var text = Describe([], inPageOrder);

        var loaded = service.GetLibraries().Where(l => !l.BuiltIn).ToList();
        Assert.Equal(loaded.Select(l => l.Id), GlossaryHint.InLoadOrder(inPageOrder).Select(l => l.Id));
        var sent = SentByDictation([], loaded);
        Assert.NotEqual(sent, CleanupPrompt.CountGlossary(DictionaryLibraryComposer.ComposeLibraries(inPageOrder)).Included);
        Assert.Contains($"receives the first {N(sent)} of 1,200 terms", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_row_neither_counts_nor_keeps_a_library_term_out_of_the_glossary()
    {
        // Dictation reads only enabled entries, so a switched-off personal entry does not shadow the
        // library term with the same spoken form.
        var text = Describe([Row("azure", "AZURE-mine", enabled: false)], [Library("team", DictionaryEntry.New("azure", "Azure"))]);

        Assert.StartsWith("0 of 1 entries enabled plus 1 from enabled libraries.", text, StringComparison.Ordinal);
        Assert.Contains("Your AI provider receives that term as vocabulary", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_send_is_said_plainly()
    {
        Assert.Equal("0 of 0 entries enabled. AI cleanup receives no vocabulary.", Describe([], []));
        Assert.Equal(
            "1 of 1 entries enabled. It is applied on this PC. AI cleanup receives no vocabulary.",
            Describe([Row("um", string.Empty)], []));
    }

    [Fact]
    public void Templates_are_counted_as_left_out()
    {
        var text = Describe([Row("azure", "Azure"), Row("sign off", "Best regards,\nChris"), Row("footer", new string('f', 150))], []);

        Assert.Contains("Your AI provider receives that term as vocabulary", text, StringComparison.Ordinal);
        Assert.EndsWith(
            $"2 entries are left out because the written form spans more than one line or runs past {CleanupPrompt.MaxGlossaryTermChars} characters.",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_order_is_the_one_the_saved_dictionary_comes_back_in()
    {
        // SQLite's BINARY collation is code point order, which string.CompareOrdinal is not: it puts U+E000
        // to U+FFFF after the surrogate pairs. Checked against the real repository reading real rows back.
        string[] patterns =
        [
            "zeta", "Zeta", "alpha", "Alpha beta", "alpha beta", "éclair", "eclair", "東京", "tokyo", "\uE000private",
            "\uFFFDreplacement", "😀 grin", "🀄 tile", "9 lives", "10 lives", " leading", "a", "ab", "a b", "a-b",
        ];
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new DictionaryRepository(db);
        repository.AddRange([.. patterns.Reverse().Select(p => DictionaryEntry.New(p, p.ToUpperInvariant()))]);

        var stored = repository.GetEnabled().Select(e => e.Pattern).ToList();

        Assert.Equal(stored, patterns.OrderBy(p => p, SqliteBinaryCollation.Instance));
        Assert.NotEqual(stored, patterns.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void The_count_is_the_glossary_the_prompt_is_built_from()
    {
        var entries = new List<DictionaryEntry>
        {
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("a z u r e", "Azure"),
            DictionaryEntry.New("dropped", string.Empty),
            DictionaryEntry.New("off", "Off") with { Enabled = false },
            DictionaryEntry.New("quoted", "\"\"\""),
        };
        entries.AddRange(Enumerable.Range(0, 200).Select(i => DictionaryEntry.New($"spoken {i}", $"Term{i}")));

        foreach (var budget in new[] { CleanupPrompt.MaxGlossaryTermsLocal, CleanupPrompt.MaxGlossaryTermsCloud })
        {
            var count = CleanupPrompt.CountGlossary(entries, budget);

            Assert.Equal(CountLines(CleanupPrompt.BuildGlossary(entries, budget)), count.Included);
            Assert.Equal(202, count.Eligible);
        }

        Assert.Equal(new GlossaryCount(0, 0), CleanupPrompt.CountGlossary(null));
        Assert.Equal(new GlossaryCount(0, 202), CleanupPrompt.CountGlossary(entries, 0));
    }

    [Fact]
    public void The_settings_window_refreshes_the_line_from_every_control_it_reads()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));

        foreach (var handler in new[] { "AiCleanupCheck_Toggled", "AiProviderCombo_SelectionChanged", "AiPromptStyleCombo_SelectionChanged", "PostCheck_Toggled" })
        {
            var start = code.IndexOf($"private void {handler}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{handler} is missing.");
            var body = code.Substring(start, Math.Min(400, code.Length - start));
            Assert.Contains("UpdateDictionaryGlossaryHint()", body, StringComparison.Ordinal);
        }

        Assert.Contains("Checked=\"PostCheck_Toggled\" Unchecked=\"PostCheck_Toggled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PostProcessingOn: PostCheck.IsChecked == true", code, StringComparison.Ordinal);
    }

    // What dictation would send for these rows once saved: the real repository reads them back in its own
    // order, and the pipeline's composition and budget count them.
    private static int SentByDictation(IReadOnlyList<DictionaryEntryBuilder.Row> rows, IReadOnlyList<DictionaryLibrary> libraries)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new DictionaryRepository(db);
        var entries = DictionaryEntryBuilder.Build(rows).Entries;
        if (entries.Count > 0)
        {
            repository.AddRange(entries);
        }

        var vocabulary = CleanupPrompt.ComposeVocabulary(repository.GetEnabled(), DictionaryLibraryComposer.ComposeLibraries(libraries));
        return CleanupPrompt.CountGlossary(
            vocabulary, CleanupPrompt.GlossaryTermBudget(CleanupPromptStyle.Auto, CleanupProvider.AzureFoundry)).Included;
    }

    private static string Csv(string prefix, int count, int replacementLength)
    {
        var lines = new List<string> { "pattern,replacement" };
        lines.AddRange(Enumerable.Range(0, count).Select(i =>
            $"{prefix} {i:D4},{prefix[0]}{new string('r', Math.Max(0, replacementLength - 5))}{i:D4}"));
        return string.Join('\n', lines);
    }

    private static int CountLines(string glossary) =>
        glossary.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

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
