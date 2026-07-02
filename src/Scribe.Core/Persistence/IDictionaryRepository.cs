using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>CRUD access to the user dictionary used by the post-processor.</summary>
public interface IDictionaryRepository
{
    /// <summary>All entries, ordered by pattern.</summary>
    IReadOnlyList<DictionaryEntry> GetAll();

    /// <summary>Only entries with <see cref="DictionaryEntry.Enabled"/> set.</summary>
    IReadOnlyList<DictionaryEntry> GetEnabled();

    /// <summary>Inserts a new entry and returns it with its assigned id.</summary>
    DictionaryEntry Add(DictionaryEntry entry);

    /// <summary>Updates an existing entry by id.</summary>
    void Update(DictionaryEntry entry);

    /// <summary>Deletes an entry by id.</summary>
    void Delete(long id);

    /// <summary>
    /// Replaces the stored dictionary with the supplied entries in a single transaction: entries
    /// with Id 0 are inserted, entries with an id are updated, and stored rows missing from the
    /// list are deleted. All-or-nothing, so a mid-save failure never leaves a half-applied edit.
    /// </summary>
    void SaveAll(IReadOnlyList<DictionaryEntry> entries);

    /// <summary>Inserts the supplied seed entries only when the table is empty; returns rows added.</summary>
    int SeedIfEmpty(IEnumerable<DictionaryEntry> entries);
}
