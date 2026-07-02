using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>Access to the voice snippets expanded by the post-processor.</summary>
public interface ISnippetRepository
{
    /// <summary>All snippets, ordered by phrase.</summary>
    IReadOnlyList<Snippet> GetAll();

    /// <summary>Only snippets with <see cref="Snippet.Enabled"/> set.</summary>
    IReadOnlyList<Snippet> GetEnabled();

    /// <summary>
    /// Replaces the stored snippets with the supplied list in a single transaction: Id 0 rows are
    /// inserted, existing ids updated, and stored rows missing from the list deleted.
    /// </summary>
    void SaveAll(IReadOnlyList<Snippet> snippets);
}
