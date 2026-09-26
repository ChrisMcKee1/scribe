namespace Scribe.Core.Libraries;

/// <summary>
/// The old Settings window's Libraries page in a build that carries the library model (W1b) without the Settings
/// redesign's Word packs page (W2): what the page may still do, the selection it judges by, and what it says.
/// </summary>
/// <remarks>
/// <para>
/// Once the library state is stored, only a library Save or an adoption writes the settings document's enabled-library
/// list, and a settings-only <c>SaveBundle</c> keeps the stored one and hands it back, so the old page can no longer store
/// a switch. It went on judging the Dictionary page's badges, the glossary count, the Save prompt's redundant entries and
/// the cleanup against its check boxes, said "Settings saved." and kept the ticks: an imported pack stored off but ticked
/// made a personal correction look redundant, the prompt removed it, and after the Save neither the pack nor the
/// dictionary wrote it (integration review, A1 and G1). So the page's switches are read-only, the Dictionary page's badges
/// and glossary count are judged against <see cref="CommittedIds"/> and never the rows, the cleanup never switches a
/// library off or copies a library's terms, and after every Save the rows show the stored list again.
/// </para>
/// <para>
/// The committed selection is the stored list as the window last read it: when its catalog load finished
/// (<see cref="CatalogLoaded"/>), since that load runs the adoption, which turns a file changed outside Scribe off and
/// patches the stored list, and then as each Save hands it back. It is not what the next Save will use: an adoption after
/// the catalog load (a library file replaced, removed or unreadable on disk while the window is open) reaches it only at
/// that Save. So nothing on the page acts on it: the page never removes a personal entry because a library covers it
/// (<see cref="RemovesCoveredEntries"/>; round 3, Astra's A2), and the selection only draws figures. The Word packs page,
/// whose Save checks the library generation, replaces the old page, and this type goes with it.
/// </para>
/// </remarks>
public sealed class LegacyLibraryPageContainment
{
    /// <summary>The page's subtitle, without the advice to turn packs on and save.</summary>
    public const string PageSubtitle =
        "Ready-made vocabulary packs help Scribe spell technical terms the way you say them: 'a p i m' becomes 'APIM', "
        + "'gpt five six terra' becomes 'GPT-5.6-Terra'. The two AI packs are on by default and the rest are opt-in. Libraries "
        + "that are on layer on top of your dictionary, and your own entries always win. Import a CSV to add your own.";

    /// <summary>The line the page shows under its subtitle.</summary>
    public const string PageNotice =
        "In this version the On column shows which libraries are switched on but can't change them: switching libraries on "
        + "or off comes with the new Word packs page. Importing, exporting and removing libraries work as before.";

    /// <summary>The page's tip, without the advice to tick libraries and save.</summary>
    public const string PageTip = "Tip: click a library to preview its terms on the right.";

    /// <summary>The Remove button's tooltip, without the advice to turn a built-in library off.</summary>
    public const string RemoveToolTip = "Remove the selected imported library. Built-in libraries can't be removed.";

    /// <summary>What a Save says, in place of "Settings saved.", when a row showed a library other than as stored.</summary>
    public const string SavedWithStoredSwitches =
        "Settings saved. The On column now shows the libraries as they are stored: this page can't switch libraries on or "
        + "off, which comes with the new Word packs page.";

    /// <summary>What the dictionary cleanup says about libraries, which it leaves as they are.</summary>
    public const string CleanupLibrariesNote =
        "Libraries can't be switched off here yet, so the cleanup leaves them as they are: that comes with the new Word packs "
        + "page.";

    // DictionaryUsageAnalyzer's summary for too little history ends by pointing at the switches this page no longer has.
    private const string AnalyzerSwitchAdvice = " You can still turn libraries off by hand on the Libraries page.";

    private List<string> _committed;
    private HashSet<string> _committedSet;

    /// <param name="storedIds">The enabled-library list of the settings the window loaded.</param>
    public LegacyLibraryPageContainment(IEnumerable<string?>? storedIds) => (_committed, _committedSet) = Distinct(storedIds);

    /// <summary>
    /// Whether the page removes a personal entry because a library covers it: never, while this type exists. The library can
    /// be switched off behind the window, by the adoption its own catalog load runs or by one while it is open, and a Save
    /// that removed the entry would leave neither the library nor the dictionary writing the term (round 3, Astra's A2). The
    /// Dictionary page's badges still show the overlap.
    /// </summary>
    public static bool RemovesCoveredEntries => false;

    /// <summary>
    /// The libraries the Dictionary page's coverage badges and glossary count are judged against: the stored list as the
    /// window last read it, in its stored order without repeats, never the page's rows.
    /// </summary>
    public IReadOnlyList<string> CommittedIds => _committed;

    /// <summary>Whether <paramref name="id"/> is in <see cref="CommittedIds"/>, compared without case, as the stored list is read.</summary>
    public bool IsCommitted(string? id) => id is not null && _committedSet.Contains(id);

    /// <summary>
    /// The stored settings document's enabled-library list, read without <c>Load</c>'s side effects, or null when the document
    /// cannot be used (a session on defaults, whose Save writes the window's own list). For <see cref="CatalogLoaded"/>, read
    /// after the catalog load.
    /// </summary>
    public static IReadOnlyList<string>? StoredIds(Persistence.ISettingsRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ReadLibrarySettings().DocumentEnabledIds;
    }

    /// <summary>
    /// After the catalog load that lists the page's libraries, before any row is shown: that load runs the adoption, which
    /// can turn a pack off and patch the stored list, so the list read after it (<see cref="StoredIds"/>) replaces the one the
    /// window was opened with. Null, for a document that cannot be used, keeps it.
    /// </summary>
    public void CatalogLoaded(IEnumerable<string?>? storedIds)
    {
        if (storedIds is not null)
        {
            (_committed, _committedSet) = Distinct(storedIds);
        }
    }

    /// <summary>
    /// After a Save that stored the document: the enabled-library list the save handed back becomes
    /// <see cref="CommittedIds"/>, and every row is to show <see cref="IsCommitted"/> again. Returns whether a row showed
    /// otherwise, which the Save says (<see cref="SavedWithStoredSwitches"/>) instead of an unqualified "Settings saved.".
    /// </summary>
    /// <param name="shownRows">Each row's library id, and whether its box showed the library on.</param>
    /// <param name="storedIds">The enabled-library list of the document the save handed back.</param>
    public bool AfterSave(IEnumerable<KeyValuePair<string, bool>> shownRows, IEnumerable<string?>? storedIds)
    {
        ArgumentNullException.ThrowIfNull(shownRows);
        (_committed, _committedSet) = Distinct(storedIds);
        var differed = false;
        foreach (var (id, on) in shownRows)
        {
            differed |= on != IsCommitted(id);
        }

        return differed;
    }

    /// <summary>What the page says after importing a library, which is stored and switched off.</summary>
    public static string Imported(string name, int terms) =>
        $"Imported \"{name}\" with {terms:N0} {(terms == 1 ? "term" : "terms")}. It is stored and switched off: switching "
        + "libraries on comes with the new Word packs page.";

    /// <summary>What the page says when Remove is asked of a built-in library.</summary>
    public static string BuiltInRemoveRefused(string name) =>
        $"\"{name}\" is built in and can't be removed. Switching libraries off comes with the new Word packs page.";

    /// <summary>
    /// The dictionary cleanup's result: how many of the user's own entries it turned off or removed (the only thing it
    /// changes in this build), then <see cref="CleanupLibrariesNote"/>.
    /// </summary>
    public static string CleanupResult(int entries, bool deleted) =>
        entries <= 0
            ? CleanupLibrariesNote
            : $"{entries:N0} {(entries == 1 ? "entry" : "entries")} {(deleted ? "removed" : "turned off")}. Review the change, "
                + $"then save to apply it. {CleanupLibrariesNote}";

    /// <summary>
    /// The cleanup's message when it has nothing to offer: <paramref name="summary"/>, the analyzer's own, without its advice
    /// to turn libraries off by hand, then <see cref="CleanupLibrariesNote"/>.
    /// </summary>
    public static string CleanupMessage(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return $"{summary.Replace(AnalyzerSwitchAdvice, string.Empty, StringComparison.Ordinal)} {CleanupLibrariesNote}";
    }

    private static (List<string> Ids, HashSet<string> Set) Distinct(IEnumerable<string?>? ids)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var id in ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id) && set.Add(id))
            {
                list.Add(id);
            }
        }

        return (list, set);
    }
}
