using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the first-run seed dictionary. A seeded entry is an always-replace applied to every
/// dictation, so it overrides the speech model's own context-sensitive casing. The seed is
/// therefore held to one rule: the spoken form must never be correct English on its own. Domain
/// vocabulary belongs in an opt-in library, not in everyone's dictionary.
/// </summary>
public sealed class DefaultVocabularyTests
{
    // Ordinary English words. Seeding any of these rewrites a sentence about a colour, a
    // metalworks, a bird or a pet, and the user cannot tell where the change came from.
    private static readonly string[] EnglishWords =
    [
        "azure", "foundry", "parakeet", "back", "re back", "net", "python", "swift", "rust",
        "go", "ruby", "flash", "spark", "hive", "pig", "storm", "kafka", "mercury",
    ];

    // Scribe's own dependencies. Users dictate about their work, not about Scribe's internals.
    private static readonly string[] ScribeInternals =
    [
        "onnx", "wasapi", "silero", "sherpa onnx", "parakeet", "velopack", "sqlite",
    ];

    [Fact]
    public void Seed_never_replaces_an_ordinary_english_word()
    {
        foreach (var entry in DefaultVocabulary.Entries)
        {
            Assert.DoesNotContain(entry.Pattern.ToLowerInvariant(), EnglishWords);
        }
    }

    [Fact]
    public void Seed_does_not_ship_scribe_implementation_vocabulary()
    {
        foreach (var entry in DefaultVocabulary.Entries)
        {
            Assert.DoesNotContain(entry.Pattern.ToLowerInvariant(), ScribeInternals);
        }
    }

    [Fact]
    public void Seed_stays_small_enough_to_review_by_hand()
    {
        // Not an arbitrary limit: every entry here is imposed on every user on first run, so the
        // list has to stay short enough that each one can be justified individually.
        Assert.InRange(DefaultVocabulary.Entries.Count, 1, 12);
    }

    [Fact]
    public void Seed_entries_are_unique_and_actually_change_the_text()
    {
        var spoken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in DefaultVocabulary.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Pattern));
            Assert.False(string.IsNullOrWhiteSpace(entry.Replacement));

            // A duplicate spoken form makes the winner an ordering accident.
            Assert.True(spoken.Add(entry.Pattern), $"Duplicate seed entry for '{entry.Pattern}'.");

            // An entry that produces the same text it matched is pure overhead.
            Assert.NotEqual(entry.Pattern, entry.Replacement, StringComparer.Ordinal);
        }
    }
}
