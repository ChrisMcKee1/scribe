using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// What 0.4.3 and 0.4.2 apply for a libraries folder and a settings document's enabled list: v0.4.3's
/// <c>DictionaryLibraryService.GetLibraries</c>, <c>GetEnabledLibraryEntries</c> and <c>DictionaryLibraryComposer</c>
/// in their shape at that tag, reading files with <see cref="Legacy043LibraryCsv"/>. The oracle for what an older build
/// does with what this version leaves behind: the downgrade encoding of a hand-placed twin (review finding A15), the
/// precedence of a remapped file (A16), the adoption patch of the enabled list (A5) and the upgrade property tests.
/// Never edit the logic: it stands for code already in the field.
/// </summary>
internal static class Legacy043LibrarySelection
{
    internal sealed record Library(string Id, bool BuiltIn, IReadOnlyList<DictionaryEntry> Entries);

    /// <summary>
    /// v0.4.3's <c>GetLibraries</c>: the built-ins ordered by category then name (both ordinal, case-insensitive), then
    /// every custom file in the order <c>Array.Sort(files, StringComparer.OrdinalIgnoreCase)</c> gives, each with its
    /// file name without <c>.csv</c> as its id; a file with no usable rows, or one that cannot be read, is skipped.
    /// </summary>
    internal static IReadOnlyList<Library> Libraries(string librariesDir)
    {
        var libraries = BuiltInDictionaryLibraries.All
            .OrderBy(library => library.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
            .Select(library => new Library(library.Id, BuiltIn: true, library.Entries))
            .ToList();
        if (!Directory.Exists(librariesDir))
        {
            return libraries;
        }

        var files = Directory.GetFiles(librariesDir, "*.csv");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var file = Legacy043LibraryCsv.Parse(System.IO.File.ReadAllText(path));
                if (file.Entries.Count == 0)
                {
                    continue;
                }

                libraries.Add(new Library(id, BuiltIn: false, file.Entries));
            }
            catch (Exception)
            {
                // 0.4.3 logged the failure and skipped the file.
            }
        }

        return libraries;
    }

    /// <summary>
    /// v0.4.3's <c>GetEnabledLibraryEntries</c>: every library whose id the enabled list holds (compared without case),
    /// in the order of <see cref="Libraries"/>, composed first-wins by trimmed spoken form over enabled rows only.
    /// Several libraries loaded under one id are all on or all off, because 0.4.3 has one flag per id.
    /// </summary>
    internal static IReadOnlyList<DictionaryEntry> EnabledEntries(IEnumerable<string>? enabledIds, string librariesDir)
    {
        var ids = new HashSet<string>(enabledIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DictionaryEntry>();
        foreach (var entry in Libraries(librariesDir).Where(library => ids.Contains(library.Id)).SelectMany(library => library.Entries))
        {
            if (entry is null || !entry.Enabled)
            {
                continue;
            }

            var key = entry.Pattern?.Trim();
            if (!string.IsNullOrEmpty(key) && seen.Add(key))
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
