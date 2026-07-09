using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Manages the dictionary libraries available to the app: the built-in set that ships embedded plus
/// any custom libraries the user has imported (stored as CSV files under
/// <see cref="Infrastructure.AppPaths.LibrariesDir"/>). Also composes the entries of the currently
/// enabled libraries, which the post-processor and AI glossary layer on top of the base dictionary.
/// </summary>
public interface IDictionaryLibraryService
{
    /// <summary>All libraries, built-in first then custom. Malformed custom files are skipped.</summary>
    IReadOnlyList<DictionaryLibrary> GetLibraries();

    /// <summary>
    /// The de-duplicated entries of every library the user has switched on (per the persisted
    /// enabled-set), for layering on top of the base dictionary. Empty when nothing is enabled.
    /// </summary>
    IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries();

    /// <summary>
    /// Imports a library from CSV text, writing it into the libraries folder as a new custom library
    /// and returning it. The display name comes from the file's <c>name</c> header, else
    /// <paramref name="suggestedName"/> (typically the file name). Throws if the CSV has no usable
    /// entries.
    /// </summary>
    DictionaryLibrary Import(string csv, string? suggestedName);

    /// <summary>
    /// Removes a custom library by id (deletes its file). Built-in libraries cannot be removed — turn
    /// them off in settings instead — and attempting to throws.
    /// </summary>
    void Remove(string id);
}
