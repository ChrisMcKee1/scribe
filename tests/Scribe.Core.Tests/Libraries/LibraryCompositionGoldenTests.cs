using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// Pins what the libraries decide for dictation, the Dictionary page's badges and the Save prompt against golden
/// outputs captured before the Libraries list became alphabetical (see <see cref="LibraryGolden"/>).
/// </summary>
public sealed class LibraryCompositionGoldenTests
{
    [Fact]
    public void Dictation_badges_and_prompts_match_the_outputs_captured_before_the_list_was_sorted()
    {
        using var fixture = new LibraryFixture();

        var actual = LibraryGolden.Render(fixture, CaptureCoverage, CapturePrompt);

        LibraryGolden.AssertMatchesGolden(actual);
    }

    // CAPTURE ONLY: SettingsWindow.UpdateDictionaryCoverage at d42d683, verbatim but for the row source.
    private static IReadOnlyDictionary<string, (DictionaryEntry Entry, string LibraryName)> CaptureCoverage(
        IReadOnlyList<DictionaryLibrary> loadedLibraries, IReadOnlyCollection<string> enabledIds)
    {
        var enabledLibraries = loadedLibraries
            .Where(l => enabledIds.Any(r => string.Equals(r, l.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var covering = new Dictionary<string, (DictionaryEntry Entry, string Library)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var library in enabledLibraries)
        {
            foreach (var entry in library.Entries)
            {
                if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Pattern))
                {
                    continue;
                }

                covering.TryAdd(entry.Pattern.Trim(), (entry, library.Name));
            }
        }

        return covering.ToDictionary(p => p.Key, p => (p.Value.Entry, p.Value.Library), StringComparer.OrdinalIgnoreCase);
    }

    // CAPTURE ONLY: SettingsWindow.ConfirmDictionaryOverlapAsync at d42d683, verbatim but for the row source.
    private static DictionaryOverlapReport CapturePrompt(
        IReadOnlyList<DictionaryEntry> entries, IReadOnlyList<DictionaryLibrary> loadedLibraries, IReadOnlyCollection<string> enabledIds)
    {
        var libraries = loadedLibraries
            .Where(l => enabledIds.Any(r => string.Equals(r, l.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var libraryEntries = new List<DictionaryEntry>();
        var sourceByPattern = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            foreach (var entry in library.Entries)
            {
                libraryEntries.Add(entry);
                if (!string.IsNullOrWhiteSpace(entry.Pattern))
                {
                    sourceByPattern.TryAdd(entry.Pattern.Trim(), library.Name);
                }
            }
        }

        return DictionaryLibraryOverlapAnalyzer.Analyze(entries, libraryEntries, sourceByPattern);
    }
}
