using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

/// <summary>
/// The order in which enabled libraries compete for a spoken form. After the user's own dictionary, the first library
/// in this order that has an enabled row for a spoken form supplies its rule, and the same order decides which library
/// terms fill the on-device glossary's slots. Built-in libraries come first, in the frozen <see cref="BuiltInOrder"/>;
/// custom libraries follow in the order of their file names.
/// </summary>
/// <remarks>
/// Culture-free and blind to names, so renaming a library, sorting the list on screen (<see cref="LibraryOrdering"/>),
/// or running on another machine never changes a winner. <see cref="IDictionaryLibraryService.GetLibraries"/>
/// returns libraries in this order and <see cref="DictionaryLibraryComposer.ComposeLibraries"/> applies it to whatever
/// it is given, so a caller holding libraries in display order still gets the winners dictation uses.
/// </remarks>
public static class LibraryPrecedence
{
    /// <summary>
    /// Built-in library ids in precedence order: the order 0.4.3 composed them in (category, then name, both ordinal and
    /// case-insensitive), frozen as ids so that renaming or recategorizing a built-in never moves it. Append a new
    /// built-in at the end, and never reorder or remove an id: the order decides which built-in supplies a spoken form
    /// two of them share, and which terms a default install's on-device model receives. When a built-in stops shipping,
    /// keep its id here and add it to <see cref="RetiredBuiltInIds"/>. The same lists are in
    /// tests/fixtures/libraries/built-in-precedence.json, which a test keeps equal to these. The macOS port does not
    /// read that file yet: it will in stream M1, and until then its Dictionary Libraries row in macos/PORTING-PLAN.md
    /// is stale, with nothing that keeps the Swift order equal to this one.
    /// </summary>
    public static IReadOnlyList<string> BuiltInOrder { get; } =
    [
        "ai-terminology",
        "ai-model-names",
        "data-and-ai",
        "data-engineering",
        "data-science-machine-learning",
        "github",
        "microsoft-365",
        "microsoft-azure",
        "dotnet-development",
        "modern-developer-stack",
        "software-development",
    ];

    /// <summary>
    /// Ids in <see cref="BuiltInOrder"/> whose library no longer ships; none so far. A retired id stays in the order for
    /// good, which costs nothing because precedence only compares libraries that exist, and listing it here is the only
    /// way an id in the order may be missing from the shipped libraries, so a typo cannot pass for a retirement.
    /// </summary>
    public static IReadOnlyList<string> RetiredBuiltInIds { get; } = [];

    private static readonly Dictionary<string, int> BuiltInIndex = BuiltInOrder
        .Select((id, index) => (id, index))
        .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);

    /// <summary>Compares libraries by precedence; see <see cref="Compare(string, bool, string, bool)"/>.</summary>
    public static IComparer<DictionaryLibrary> Comparer { get; } =
        Comparer<DictionaryLibrary>.Create((a, b) => Compare(a.Id, a.BuiltIn, b.Id, b.BuiltIn));

    /// <summary>
    /// Compares two libraries by precedence: listed built-ins by their place in <see cref="BuiltInOrder"/>, then any
    /// built-in the list does not name, by id, then custom libraries by file name, each tie broken by ordinal id.
    /// </summary>
    /// <remarks>
    /// Custom libraries compare as their file names (<c>id + ".csv"</c>), not as bare ids, because the loader has always
    /// read them in file-name order and the two differ where one id extends another: '-' sorts before '.', so
    /// "team-terms-2.csv", the file a second import of the same library gets, comes before "team-terms.csv", and
    /// comparing bare ids would swap which of the two wins. The ordinal tie-break only matters in a folder with
    /// case-sensitive names, where "Team.csv" and "team.csv" can both exist.
    /// </remarks>
    public static int Compare(string? id, bool builtIn, string? otherId, bool otherBuiltIn)
    {
        var rank = RankOf(id, builtIn);
        var byRank = rank.CompareTo(RankOf(otherId, otherBuiltIn));
        if (byRank != 0)
        {
            return byRank;
        }

        var byKey = rank switch
        {
            Rank.Listed => BuiltInIndex[id!].CompareTo(BuiltInIndex[otherId!]),
            Rank.Unlisted => StringComparer.OrdinalIgnoreCase.Compare(id, otherId),
            _ => StringComparer.OrdinalIgnoreCase.Compare(FileName(id), FileName(otherId)),
        };
        return byKey != 0 ? byKey : string.CompareOrdinal(id, otherId);
    }

    /// <summary>The libraries in precedence order, as a new list; null entries are dropped.</summary>
    public static IReadOnlyList<DictionaryLibrary> Order(IEnumerable<DictionaryLibrary?> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        return libraries.OfType<DictionaryLibrary>().OrderBy(library => library, Comparer).ToList();
    }

    /// <summary>
    /// Items that stand for libraries (a settings row, say) in precedence order, as a new list, for a caller that has
    /// the id and the built-in flag but not the library.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> items, Func<T, string?> id, Func<T, bool> builtIn)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(builtIn);
        return items
            .OrderBy(item => item, Comparer<T>.Create((a, b) => Compare(id(a), builtIn(a), id(b), builtIn(b))))
            .ToList();
    }

    /// <summary>
    /// The libraries whose ids are in <paramref name="enabledIds"/> (case-insensitive, the way the stored enabled set is
    /// read), in precedence order. The order of <paramref name="enabledIds"/> itself never matters.
    /// </summary>
    public static IReadOnlyList<DictionaryLibrary> Enabled(
        IEnumerable<DictionaryLibrary?>? libraries, IEnumerable<string?>? enabledIds)
    {
        if (libraries is null || enabledIds is null)
        {
            return [];
        }

        var ids = new HashSet<string>(enabledIds.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return ids.Count == 0 ? [] : Order(libraries.Where(library => library is not null && ids.Contains(library.Id)));
    }

    // A built-in the frozen list does not name can only come from a build that added a CSV without appending its id,
    // which a test refuses; it still gets a fixed place, after the listed built-ins and before every custom library.
    private enum Rank
    {
        Listed,
        Unlisted,
        Custom,
    }

    private static Rank RankOf(string? id, bool builtIn) =>
        !builtIn ? Rank.Custom
        : id is not null && BuiltInIndex.ContainsKey(id) ? Rank.Listed
        : Rank.Unlisted;

    private static string FileName(string? id) => (id ?? string.Empty) + ".csv";
}
