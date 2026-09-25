using System.Globalization;
using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>The rows of one library a search looked through.</summary>
/// <param name="LibraryId">The library.</param>
/// <param name="Rows">Its rows, in saved order.</param>
public sealed record LibrarySearchSource(string LibraryId, IReadOnlyList<DraftTermRow> Rows);

/// <summary>One library's matches, in saved order.</summary>
public sealed record LibrarySearchMatches(string LibraryId, IReadOnlyList<long> RowIds)
{
    /// <summary>How many of its rows match.</summary>
    public int Count => RowIds.Count;
}

/// <summary>
/// What "Search all libraries" found: every library searched with its matches (none included), in the order searched.
/// It never says which library to select: the selection stays where the user put it (plan 3.1, review finding I4), and a
/// selected library with no matches says where the others are (<see cref="FoundElsewhere"/>).
/// </summary>
public sealed class LibrarySearchResult
{
    private readonly Dictionary<string, LibrarySearchMatches> _byId;

    internal LibrarySearchResult(string query, IReadOnlyList<LibrarySearchMatches> libraries)
    {
        Query = query;
        Libraries = libraries;
        TotalMatches = libraries.Sum(library => library.Count);
        _byId = new Dictionary<string, LibrarySearchMatches>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            _byId.TryAdd(library.LibraryId, library);
        }
    }

    /// <summary>The query as searched: trimmed; empty when the search is not active.</summary>
    public string Query { get; }

    /// <summary>Whether a search is active; while it is not, nothing is filtered and no counts are shown.</summary>
    public bool IsActive => Query.Length > 0;

    /// <summary>Every library searched, in the order searched, with its matches.</summary>
    public IReadOnlyList<LibrarySearchMatches> Libraries { get; }

    /// <summary>Matches across every library.</summary>
    public int TotalMatches { get; }

    /// <summary>How many rows of the library match; 0 for one the search did not look through.</summary>
    public int CountIn(string libraryId) => _byId.TryGetValue(libraryId, out var matches) ? matches.Count : 0;

    /// <summary>The library's matching rows, in saved order.</summary>
    public IReadOnlyList<long> MatchesIn(string libraryId) =>
        _byId.TryGetValue(libraryId, out var matches) ? matches.RowIds : [];

    /// <summary>
    /// The other libraries with matches, in the order searched, for "No matches in GitHub. Found in: Microsoft 365 and
    /// Products (1)".
    /// </summary>
    public IReadOnlyList<LibrarySearchMatches> FoundElsewhere(string selectedLibraryId) =>
        Libraries
            .Where(library => library.Count > 0
                && !string.Equals(library.LibraryId, selectedLibraryId, StringComparison.OrdinalIgnoreCase))
            .ToList();
}

/// <summary>
/// "Search all libraries" (plan 3.1): Spoken or Written containing the query, case- and accent-insensitive by the
/// culture captured when the page loads, per library.
/// </summary>
/// <remarks>
/// Matching is <see cref="CompareInfo.IndexOf(string, string, CompareOptions)"/> with
/// <see cref="CompareOptions.IgnoreCase"/> and <see cref="CompareOptions.IgnoreNonSpace"/> under that culture, so what
/// counts as the same letter follows the user's language: "resume" finds "résumé" in English, "istanbul" finds
/// "İstanbul" in Turkish, and in Swedish "a" does not find "å", which is a letter of its own there. A query that is
/// empty, blank, or made only of characters the comparison ignores is no search. Pure and thread-safe: the overload that
/// takes <see cref="LibrarySearchSource"/>s reads only the immutable row lists it is given, so the shell can take them
/// on the dispatcher and search on a worker. Measured in the test host at 100,000 terms: about 30 ms for an ASCII
/// query and 170 ms for one with accents, which takes ICU's slower path for every row, so at that size the search
/// belongs off the dispatcher; at 10,000 terms both stay under 25 ms.
/// </remarks>
public sealed class LibrarySearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private readonly CompareInfo _compareInfo;

    private LibrarySearch(CultureInfo culture)
    {
        Culture = culture;
        _compareInfo = culture.CompareInfo;
    }

    /// <summary>The culture whose rules this instance matches by.</summary>
    public CultureInfo Culture { get; }

    /// <summary>A search for the culture the current thread uses, fixed from now on.</summary>
    public static LibrarySearch ForCurrentCulture() => new(CultureInfo.CurrentCulture);

    /// <summary>A search for <paramref name="culture"/>.</summary>
    public static LibrarySearch For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new LibrarySearch(culture);
    }

    /// <summary>The query as it would be searched: trimmed, or empty when it is no search.</summary>
    public string Normalize(string? query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        return trimmed.Length == 0 || _compareInfo.Compare(trimmed, string.Empty, Options) == 0 ? string.Empty : trimmed;
    }

    /// <summary>Whether a row's Spoken or Written value contains <paramref name="query"/>.</summary>
    public bool Matches(TermValues values, string? query)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = Normalize(query);
        return normalized.Length > 0 && MatchesNormalized(values, normalized);
    }

    /// <summary>
    /// Searches every library of the workspace's draft that is not deleted, in precedence order. It reads the workspace,
    /// so it runs where the workspace does, on the dispatcher; to search on a worker, take the sources there first.
    /// </summary>
    public LibrarySearchResult Search(LibraryWorkspace workspace, string? query)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Search(
            workspace.Draft.Libraries
                .Where(library => !library.PendingDelete)
                .Select(library => new LibrarySearchSource(library.Content.Id, workspace.RowsOf(library.Content.Id))),
            query);
    }

    /// <summary>Searches <paramref name="libraries"/>, in the order given.</summary>
    public LibrarySearchResult Search(IEnumerable<LibrarySearchSource> libraries, string? query)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var normalized = Normalize(query);
        var results = new List<LibrarySearchMatches>();
        foreach (var library in libraries)
        {
            if (library is null)
            {
                continue;
            }

            List<long>? matches = null;
            if (normalized.Length > 0)
            {
                foreach (var row in library.Rows)
                {
                    if (MatchesNormalized(row.Row.Values, normalized))
                    {
                        (matches ??= []).Add(row.RowId);
                    }
                }
            }

            results.Add(new LibrarySearchMatches(library.LibraryId, matches is null ? [] : matches));
        }

        return new LibrarySearchResult(normalized, results);
    }

    private bool MatchesNormalized(TermValues values, string query) =>
        _compareInfo.IndexOf(values.Spoken, query, Options) >= 0 || _compareInfo.IndexOf(values.Written, query, Options) >= 0;
}
