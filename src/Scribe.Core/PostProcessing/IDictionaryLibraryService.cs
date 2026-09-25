using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Manages the dictionary libraries available to the app: the built-in set that ships embedded plus
/// any custom libraries the user has imported (stored as CSV files under
/// <see cref="Infrastructure.AppPaths.LibrariesDir"/>). Also composes the entries of the currently
/// enabled libraries, which the post-processor and AI glossary layer on top of the base dictionary.
/// The implementation keeps the libraries through the Save journal of <see cref="Libraries.ILibraryCatalogStore"/>,
/// so <see cref="Import"/> and <see cref="Remove"/> are one-operation Saves that follow a Save's rules.
/// </summary>
public interface IDictionaryLibraryService
{
    /// <summary>
    /// All libraries in precedence order (<see cref="Libraries.LibraryPrecedence"/>): built-in first, then custom by
    /// file name. That is the order they compete for a spoken form in, not the order the Libraries list shows, which
    /// the view sorts with <see cref="Libraries.LibraryOrdering"/>. Each carries its effective entries: a built-in with
    /// the user's edits applied, and a paused library (a file that cannot be read, one another app holds open at a start
    /// that has read none, or one written by a newer Scribe) with none; an empty custom library is listed too. A custom
    /// library is listed under the id release 0.4.4 gave it, its file's stem, which is what the stored enabled list
    /// names; <see cref="Libraries.ILibraryCatalogStore.LoadCatalog"/> carries its logical id.
    /// </summary>
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
    /// <paramref name="suggestedName"/> (typically the file name) unless it is blank or not well-formed
    /// UTF-16, as a file name can be. The library is created through the journal and committed as a
    /// generation of its own, switched off and kept from AI cleanup. Throws
    /// <see cref="InvalidOperationException"/> if the CSV has invalid rows or no usable entries, while
    /// another library change is still being saved or its outcome is unresolved, while the session runs
    /// on defaults because the saved settings could not be used, and while the libraries were changed by
    /// a newer Scribe.
    /// </summary>
    DictionaryLibrary Import(string csv, string? suggestedName);

    /// <summary>
    /// Removes a custom library by id: its file moves to Recently deleted through the journal, where it
    /// can be restored until retention expires it, so it is absent from the libraries folder either way.
    /// An id no library has removes nothing. Built-in libraries cannot be removed; turn them off in
    /// settings instead, and attempting to throws. It also throws <see cref="InvalidOperationException"/>
    /// while the library's file cannot be read, and for the reasons <see cref="Import"/> gives that are not
    /// about the CSV.
    /// </summary>
    void Remove(string id);
}
