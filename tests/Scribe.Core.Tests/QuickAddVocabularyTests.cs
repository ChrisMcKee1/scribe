using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddVocabularyTests
{
    [Fact]
    public void Personal_wins_over_packs()
    {
        var vocabulary = QuickAddVocabulary.Compose(
            [Entry("cloud", "Personal")],
            [],
            [Library("ai-terminology", "AI terminology", [Entry("cloud", "Pack")])],
            ["ai-terminology"]);

        Assert.NotNull(vocabulary.FindPersonal(" cloud "));
        Assert.Null(vocabulary.FindPack("cloud"));
    }

    [Fact]
    public void First_pack_in_precedence_wins()
    {
        var vocabulary = QuickAddVocabulary.Compose(
            [],
            [],
            [Library("ai-model-names", "AI models", [Entry("copilot", "Model")]), Library("ai-terminology", "AI terms", [Entry("copilot", "Term")])],
            ["ai-model-names", "ai-terminology"]);

        var pack = vocabulary.FindPack("copilot");
        Assert.NotNull(pack);
        Assert.Equal("AI terms", pack.PackName);
        Assert.Equal("Term", pack.Entry.Replacement);
    }

    [Fact]
    public void Disabled_pack_entries_are_ignored()
    {
        var vocabulary = QuickAddVocabulary.Compose([], [], [Library("ai-terminology", "AI", [Entry("cloud", "Copilot", enabled: false)])], ["ai-terminology"]);

        Assert.Null(vocabulary.FindPack("cloud"));
    }

    [Fact]
    public void Pending_forms_compare_trimmed_and_case_insensitive()
    {
        var vocabulary = QuickAddVocabulary.Compose([], [" Cloud Pilot "], [], []);

        Assert.Contains("cloud pilot", vocabulary.PendingSpokenForms);
        Assert.True(vocabulary.PendingSpokenForms.Contains("CLOUD PILOT"));
    }

    private static DictionaryEntry Entry(string pattern, string replacement, bool enabled = true) => new(0, pattern, replacement, Enabled: enabled);
    private static DictionaryLibrary Library(string id, string name, IReadOnlyList<DictionaryEntry> entries) => new(id, name, "Built-in", null, true, entries);
}
