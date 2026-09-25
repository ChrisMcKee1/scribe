using System.Globalization;
using System.Text;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The Libraries list now shows an A to Z order that is not the order libraries compete in. These tests hand every
/// consumer that picks a winner the libraries in that display order, in reverse, and in many random orders, and
/// require the same winners, glossary, badges, Save prompt, saved enabled list and cleanup copies each time.
/// </summary>
public sealed class LibraryOrderInvariantTests
{
    [Fact]
    public void No_order_of_the_libraries_changes_a_winner_the_glossary_a_badge_or_the_Save_prompt()
    {
        using var fixture = new LibraryFixture();
        var precedence = fixture.Service.GetLibraries();
        var display = LibraryOrdering.For(CultureInfo.GetCultureInfo("en-US")).Sort(precedence, l => l.Name, l => l.Id);

        // The display order must really differ from precedence, among the built-ins and among the custom libraries,
        // or this test would prove nothing.
        Assert.NotEqual(precedence.Where(l => l.BuiltIn).Select(l => l.Id), display.Where(l => l.BuiltIn).Select(l => l.Id));
        Assert.NotEqual(precedence.Where(l => !l.BuiltIn).Select(l => l.Id), display.Where(l => !l.BuiltIn).Select(l => l.Id));

        var random = new Random(20260924);
        var orders = new List<IReadOnlyList<DictionaryLibrary>> { display, Enumerable.Reverse(precedence).ToList() };
        for (var i = 0; i < 30; i++)
        {
            orders.Add(precedence.OrderBy(_ => random.Next()).ToList());
        }

        var personal = fixture.Dictionary.GetAll();
        var personalEnabled = fixture.Dictionary.GetEnabled();
        foreach (var (scenario, enabledIds) in LibraryFixture.Scenarios)
        {
            var expected = Outcome(precedence, enabledIds, personal, personalEnabled);
            foreach (var order in orders)
            {
                var storedIds = enabledIds.OrderBy(_ => random.Next()).ToArray();
                var actual = Outcome(order, storedIds, personal, personalEnabled);
                Assert.True(expected == actual, $"{scenario}: the outcome changed with the libraries in the order " +
                    string.Join(", ", order.Select(l => l.Id)));
            }
        }
    }

    [Fact]
    public void Composing_the_rows_in_display_order_gives_the_rules_the_service_gives_dictation()
    {
        using var fixture = new LibraryFixture();
        var display = LibraryOrdering.For(CultureInfo.GetCultureInfo("en-US"))
            .Sort(fixture.Service.GetLibraries(), l => l.Name, l => l.Id);

        foreach (var (scenario, enabledIds) in LibraryFixture.Scenarios)
        {
            fixture.Enable(enabledIds);
            var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);

            var fromDisplay = DictionaryLibraryComposer.ComposeLibraries(display.Where(l => enabled.Contains(l.Id)));

            Assert.True(
                fixture.Service.GetEnabledLibraryEntries().SequenceEqual(fromDisplay),
                $"{scenario}: composing in display order gave different rules from the service");
        }
    }

    [Fact]
    public void No_order_of_the_catalog_its_markers_or_a_draft_changes_a_tiered_winner_a_status_the_glossary_or_the_cleanup()
    {
        // W1b's composition (acceptance C-2): the catalog's libraries and the state's markers in random orders, committed and
        // previewed, give the same rules, AI subset, glossaries, badges, Save prompt, statuses and dictionary cleanup plan.
        using var fixture = new LibraryFixture();
        var personal = fixture.Dictionary.GetEnabled();
        var personalAll = fixture.Dictionary.GetAll();
        var random = new Random(20260925);
        foreach (var (scenario, enabledIds) in LibraryFixture.Scenarios)
        {
            var catalog = fixture.AdoptedCatalog(enabledIds);
            Assert.NotEmpty(catalog.LocalState.LegacyMarkers);
            var expected = TieredOutcome(LibraryComposition.Committed(catalog, personal, new GlossaryBudget(80)), catalog, personalAll);
            for (var i = 0; i < 12; i++)
            {
                var state = catalog.LocalState;
                var shuffled = LibraryLocalState.Create(
                    state.EnabledIds.OrderBy(_ => random.Next()),
                    state.LegacyEnabledIds.OrderBy(_ => random.Next()),
                    state.AiPermissions.OrderBy(_ => random.Next()),
                    state.LegacyMarkers.OrderBy(_ => random.Next()),
                    state.AiUpgradeNotice.OrderBy(_ => random.Next()),
                    state.Health,
                    state.AcceptedContent.OrderBy(_ => random.Next()),
                    state.AiPermissionsLost);
                var libraries = catalog.Libraries.OrderBy(_ => random.Next()).ToList();
                var permuted = new LibraryCatalog(catalog.Generation, libraries, shuffled, [], [], 0);
                var draft = new LibraryDraft(
                    3, catalog.Generation,
                    [.. libraries.Select(l => new DraftLibrary(l.Content, LibraryOrigin.Existing, l.State, FileName: l.FileName))],
                    shuffled, []);

                Assert.True(expected == TieredOutcome(LibraryComposition.Committed(permuted, personal, new GlossaryBudget(80)), catalog, personalAll),
                    $"{scenario}: a committed composition changed with the libraries in the order " + string.Join(", ", libraries.Select(l => l.Content.Id)));
                Assert.True(expected == TieredOutcome(LibraryComposition.Preview(draft, permuted, personal, new GlossaryBudget(80)), catalog, personalAll),
                    $"{scenario}: a preview changed with the libraries in the order " + string.Join(", ", libraries.Select(l => l.Content.Id)));
            }
        }
    }

    // Everything the composition decides, rendered so two compositions compare as text.
    private static string TieredOutcome(LibraryComposition composition, LibraryCatalog catalog, IReadOnlyList<DictionaryEntry> personalAll)
    {
        var text = new StringBuilder();
        text.AppendLine("rules:");
        foreach (var rule in composition.Rules)
        {
            text.AppendLine($"{rule.Key.Value}|{rule.Entry.Replacement}|{rule.Entry.WholeWord}|{rule.LibraryId}|{rule.Tier}|{rule.LegacyMarkerActive}");
        }

        text.AppendLine("ai: " + string.Join("|", composition.AiLibraryEntries.Select(e => e.Pattern)));
        text.AppendLine($"markers active: {composition.AnyLegacyMarkerActive}");
        var vocabulary = CleanupPrompt.ComposeVocabulary(personalAll.Where(e => e.Enabled).ToList(), composition.AiLibraryEntries);
        text.AppendLine(CleanupPrompt.BuildGlossary(vocabulary, CleanupPrompt.MaxGlossaryTermsLocal));
        text.AppendLine(CleanupPrompt.BuildGlossary(vocabulary, CleanupPrompt.MaxGlossaryTermsCloud));
        foreach (var (key, hit) in composition.Coverage().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"badge {key}|{hit.LibraryId}|{hit.LibraryName}|{hit.Entry.Replacement}|{hit.FileName}");
        }

        foreach (var overlap in composition.OverlapReport(personalAll).Overlaps)
        {
            text.AppendLine($"prompt {overlap.Kind}|{overlap.Pattern}|{overlap.LibraryId}");
        }

        foreach (var library in catalog.Libraries.OrderBy(l => l.Content.Id, StringComparer.Ordinal))
        {
            foreach (var row in library.Content.Rows)
            {
                var status = composition.StatusOf(library.Content.Id, row.Key);
                text.AppendLine(
                    $"status {library.Content.Id}|{row.Key.Value}|{status.Marker}|{status.Winner}|{status.WinningLibraryId}|" +
                    $"{string.Join(",", status.SameResultIn)}|{string.Join(",", status.DifferentResultIn)}|{status.Glossary}");
            }
        }

        var cleanup = LibrarySwitchOffCopy.Plan(
            personalAll.Select(e => new LibrarySwitchOffCopy.Row(e.Pattern, e.Replacement, e.WholeWord, e.Enabled)),
            composition,
            composition.EnabledLibraries
                .Where(l => !l.BuiltIn)
                .Select(l => new LibraryUsage(l.Id, l.Name, [.. l.EnabledEntries], UnusedCount: 1, l.BuiltIn)));
        text.AppendLine("cleanup: " + string.Join(",", cleanup.Copies.Select(e => e.Pattern)) + $"; {cleanup.Collided}; " +
            string.Join(",", cleanup.KeptOn.Select(k => k.Id)));
        return text.ToString();
    }

    private static string Outcome(
        IReadOnlyList<DictionaryLibrary> libraries,
        IReadOnlyCollection<string> enabledIds,
        IReadOnlyList<DictionaryEntry> personal,
        IReadOnlyList<DictionaryEntry> personalEnabled)
    {
        var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        var composed = DictionaryLibraryComposer.ComposeLibraries(libraries.Where(l => enabled.Contains(l.Id)));
        var effective = DictionaryLibraryComposer.Merge(personalEnabled, composed);
        var coverage = DictionaryLibraryOverlapAnalyzer.Coverage(libraries, enabledIds);
        var report = DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries(personal, libraries, enabledIds);
        var saved = LibraryPrecedence.Order(libraries.Where(l => enabled.Contains(l.Id)), l => l.Id, l => l.BuiltIn);

        // The dictionary cleanup switching off every enabled custom library while the built-ins stay on, with the review
        // listing them in whatever order they arrive here.
        var cleanup = LibrarySwitchOffCopy.Plan(
            personal.Select(e => new LibrarySwitchOffCopy.Row(e.Pattern, e.Replacement, e.WholeWord, e.Enabled)),
            libraries,
            libraries.Select(l => new LibrarySwitchOffCopy.LibraryRow(l.Id, l.BuiltIn, enabled.Contains(l.Id))),
            libraries
                .Where(l => enabled.Contains(l.Id) && !l.BuiltIn)
                .Select(l => new LibraryUsage(l.Id, l.Name, [.. l.EnabledEntries], UnusedCount: 1, l.BuiltIn)));

        var text = new StringBuilder();
        text.AppendLine("rules, in the order dictation and the glossary walk them:");
        foreach (var entry in composed)
        {
            text.Append(entry.Pattern).Append('|').Append(entry.Replacement).Append('|').Append(entry.WholeWord).AppendLine();
        }

        text.AppendLine("on-device glossary:").AppendLine(CleanupPrompt.BuildGlossary(effective, CleanupPrompt.MaxGlossaryTermsLocal));
        text.AppendLine("cloud glossary:").AppendLine(CleanupPrompt.BuildGlossary(effective, CleanupPrompt.MaxGlossaryTermsCloud));
        text.AppendLine("badges:");
        foreach (var (key, hit) in coverage.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"{key}|{hit.LibraryId}|{hit.LibraryName}|{hit.Entry.Replacement}|{hit.Entry.WholeWord}");
        }

        text.AppendLine("Save prompt:");
        foreach (var overlap in report.Overlaps)
        {
            text.AppendLine($"{overlap.Kind}|{overlap.Pattern}|{overlap.Replacement}|{overlap.LibraryReplacement}|{overlap.LibraryId}");
        }

        // The order the window's one-line collector saves the enabled ids in (it calls the same LibraryPrecedence.Order).
        text.AppendLine("saved enabled list:").AppendLine(string.Join(",", saved.Select(l => l.Id)));
        text.AppendLine("cleanup copies:");
        foreach (var entry in cleanup.Copies)
        {
            text.Append(entry.Pattern).Append('|').Append(entry.Replacement).Append('|').Append(entry.WholeWord).AppendLine();
        }

        text.AppendLine($"cleanup collisions: {cleanup.Collided}");
        text.AppendLine("cleanup kept on: " + string.Join(",", cleanup.KeptOn.Select(k => $"{k.Id}|{k.BuiltIn}|{k.OverlappingTerms}")));
        return text.ToString();
    }
}
