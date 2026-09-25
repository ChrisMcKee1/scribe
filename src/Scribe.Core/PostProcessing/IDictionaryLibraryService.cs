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
    /// The de-duplicated entries of every library the stored settings switch on, for a caller that has no settings in
    /// use to pass. None while the stored settings cannot be used (<see cref="Persistence.ISettingsRepository.LastLoadFailed"/>):
    /// the defaults standing in for an unreadable or lost document are not the user's choice. Dictation never asks
    /// here; it passes its own selection to <see cref="GetEnabledLibraryEntries(IReadOnlyCollection{string})"/>.
    /// </summary>
    IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries();

    /// <summary>
    /// The de-duplicated entries of the libraries <paramref name="enabledIds"/> names, for layering on top of the base
    /// dictionary; empty when it names none. The one place a library selection becomes entries: the post-processor, the
    /// AI cleanup glossary, the usage report and quick add each pass the enabled ids of the settings dictation runs on,
    /// never a fresh read of the stored document, so a document that turns unreadable mid-session cannot swap in the
    /// default selection. The dictionary library program replaces this seam with a vocabulary source.
    /// </summary>
    IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds);

    /// <summary>
    /// Imports a library from CSV text, writing it into the libraries folder as a new custom library
    /// and returning it. The display name comes from the file's <c>name</c> header, else
    /// <paramref name="suggestedName"/> (typically the file name). Throws if the CSV has no usable
    /// entries.
    /// </summary>
    DictionaryLibrary Import(string csv, string? suggestedName);

    /// <summary>
    /// Removes a custom library by id (deletes its file). Built-in libraries cannot be removed; turn
    /// them off in settings instead, and attempting to throws.
    /// </summary>
    void Remove(string id);
}
