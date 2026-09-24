using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// Pins what the libraries decide for dictation, the Dictionary page's badges and the Save prompt against golden
/// outputs captured before the Libraries list became alphabetical (see <see cref="LibraryGolden"/>).
/// </summary>
/// <remarks>
/// The golden file was rendered at d42d683 with the badge and prompt inputs taken from verbatim copies of the two loops
/// the Settings window ran then (<c>UpdateDictionaryCoverage</c> and <c>ConfirmDictionaryOverlapAsync</c>). It is
/// rendered here from <see cref="DictionaryLibraryOverlapAnalyzer.Coverage"/> and
/// <see cref="DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries"/>, which replaced them, so the same file proves
/// the move changed nothing.
/// </remarks>
public sealed class LibraryCompositionGoldenTests
{
    [Fact]
    public void Dictation_badges_and_prompts_match_the_outputs_captured_before_the_list_was_sorted()
    {
        using var fixture = new LibraryFixture();

        var actual = LibraryGolden.Render(
            fixture,
            (libraries, enabledIds) => DictionaryLibraryOverlapAnalyzer.Coverage(libraries, enabledIds)
                .ToDictionary(pair => pair.Key, pair => (pair.Value.Entry, pair.Value.LibraryName), StringComparer.OrdinalIgnoreCase),
            DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries);

        LibraryGolden.AssertMatchesGolden(actual);
    }
}
