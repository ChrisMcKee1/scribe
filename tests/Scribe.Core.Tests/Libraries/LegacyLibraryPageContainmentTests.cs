using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The old Settings window's Libraries page, contained until the Word packs page replaces it (integration review, A1 and
/// G1): once the library state is stored a settings-only Save keeps the stored library list, so the page's switches are
/// read-only, every figure on the page is judged against the committed selection and never the rows, the cleanup leaves
/// libraries alone, and after a Save the rows show the stored list again. Astra's scenario runs over the real settings
/// store, library service and post-processor; the window's use of <see cref="LegacyLibraryPageContainment"/> is pinned by
/// reading its source, as <see cref="CleanupDisclosureTests"/> does.
/// </summary>
public sealed class LegacyLibraryPageContainmentTests : IDisposable
{
    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly DictionaryRepository _dictionary;
    private readonly DictionaryLibraryService _libraries;
    private readonly TextPostProcessor _processor;

    public LegacyLibraryPageContainmentTests()
    {
        _database = _folder.Open();
        _settings = new SettingsRepository(_database);
        _dictionary = new DictionaryRepository(_database);
        _libraries = new DictionaryLibraryService(new AppPaths(_folder.Root), _settings, NullLogger<DictionaryLibraryService>.Instance);
        _processor = new TextPostProcessor(_dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: _libraries);
    }

    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void A_pack_ticked_on_the_old_page_neither_costs_the_personal_correction_nor_reads_as_stored()
    {
        // Stored: the default libraries, with the library state the first start's adoption records.
        _settings.Save(AppSettings.CreateDefault());
        _libraries.LoadCatalog();
        Assert.NotNull(_settings.Get(LibrarySettingKeys.State));

        // An imported pack is stored off and writes kube as Kubernetes; the user's own dictionary writes it the same way.
        var pack = _libraries.Import("pattern,replacement\nkube,Kubernetes\n", "Cluster terms");
        _dictionary.AddRange([DictionaryEntry.New("kube", "Kubernetes")]);

        // The window opens on the stored settings. On cad90f2's page the user ticks the pack and adds another entry.
        var window = _settings.Load();
        var stored = window.EnabledDictionaryLibraryIds.ToList();
        Assert.DoesNotContain(pack.Id, stored, StringComparer.OrdinalIgnoreCase);
        var page = new LegacyLibraryPageContainment(window.EnabledDictionaryLibraryIds);
        var loaded = _libraries.GetLibraries();
        var ticked = stored.Append(pack.Id).ToList();
        var entries = _dictionary.GetAll().Append(DictionaryEntry.New("quillmoor", "Quillmoor")).ToList();

        // Judged against the ticks, as that page judged it, the correction looks redundant and the prompt offers to remove
        // it; judged against the committed selection, nothing is.
        var live = DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries(entries, loaded, ticked);
        Assert.Contains(live.Redundant, overlap => overlap.Pattern == "kube");
        var committed = DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries(entries, loaded, page.CommittedIds);
        Assert.Equal(0, committed.RedundantCount);

        // The Save hands SaveBundle the rows' list, as the window does; the stored list is kept and handed back.
        window.EnabledDictionaryLibraryIds = ticked;
        _settings.SaveBundle(window, entries, null, new ExternalIntents(0, 0));
        Assert.Equal(stored, window.EnabledDictionaryLibraryIds);
        Assert.Equal(stored, _settings.Load().EnabledDictionaryLibraryIds);

        // The page says the switch was not stored, the pack's row goes back to off, and the figures follow the stored list.
        var shown = loaded
            .Select(library => new KeyValuePair<string, bool>(library.Id, ticked.Contains(library.Id, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(page.AfterSave(shown, window.EnabledDictionaryLibraryIds));
        Assert.False(page.IsCommitted(pack.Id));
        Assert.Equal(stored, page.CommittedIds);

        // Dictation after the Save, on the settings the window applies: the correction still writes Kubernetes.
        _processor.Reload(window.EnabledDictionaryLibraryIds);
        Assert.Equal("deploy it on Kubernetes and Quillmoor", _processor.Process("deploy it on kube and quillmoor"));

        // What the ticks led to on that page: the removal the prompt offered, stored by the same kind of Save, leaves neither
        // the pack nor the dictionary writing it.
        var removed = entries
            .Where(entry => !live.Redundant.Any(overlap => string.Equals(overlap.Pattern, entry.Pattern.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        window.EnabledDictionaryLibraryIds = ticked;
        _settings.SaveBundle(window, removed, null, new ExternalIntents(0, 0));
        _processor.Reload(window.EnabledDictionaryLibraryIds);
        Assert.Equal("deploy it on kube and Quillmoor", _processor.Process("deploy it on kube and quillmoor"));
    }

    [Fact]
    public void The_committed_selection_is_the_stored_list_and_only_a_save_moves_it()
    {
        var page = new LegacyLibraryPageContainment(["ai-model-names", "AI-Model-Names", " ", null, "team-terms"]);

        Assert.Equal(["ai-model-names", "team-terms"], page.CommittedIds);
        Assert.True(page.IsCommitted("AI-MODEL-NAMES"));
        Assert.True(page.IsCommitted("Team-Terms"));
        Assert.False(page.IsCommitted("github"));
        Assert.False(page.IsCommitted(null));

        Assert.False(page.AfterSave([], ["github"]));
        Assert.Equal(["github"], page.CommittedIds);
        Assert.False(page.IsCommitted("team-terms"));
        Assert.Empty(new LegacyLibraryPageContainment(null).CommittedIds);
    }

    [Theory]
    [InlineData("rows as stored", "a:on b:off", "a", false)]
    [InlineData("a pack ticked on", "a:on b:on", "a", true)]
    [InlineData("a pack ticked off", "a:off b:off", "a", true)]
    [InlineData("case differs", "A:on b:off", "a", false)]
    [InlineData("a removed library still listed", "a:on", "a b", false)]
    [InlineData("a library an adoption dropped", "a:on b:on", "a", true)]
    [InlineData("two libraries under one id", "a:on a:on b:off", "a", false)]
    public void After_a_save_every_row_follows_the_stored_list_and_a_row_that_showed_otherwise_is_reported(
        string situation, string rows, string storedAfter, bool reported)
    {
        var shown = rows.Split(' ')
            .Select(row => row.Split(':'))
            .Select(parts => new KeyValuePair<string, bool>(parts[0], parts[1] == "on"))
            .ToList();
        var stored = storedAfter.Split(' ');
        var page = new LegacyLibraryPageContainment(["a", "b"]);

        Assert.True(reported == page.AfterSave(shown, stored), situation);
        Assert.Equal(stored, page.CommittedIds);
        Assert.All(shown, row => Assert.Equal(stored.Contains(row.Key, StringComparer.OrdinalIgnoreCase), page.IsCommitted(row.Key)));
    }

    [Fact]
    public void The_texts_say_what_this_build_does()
    {
        Assert.Contains("can't change them", LegacyLibraryPageContainment.PageNotice, StringComparison.Ordinal);
        Assert.Contains("Word packs page", LegacyLibraryPageContainment.PageNotice, StringComparison.Ordinal);
        Assert.Equal(
            "Imported \"Team\" with 3 terms. It is stored and switched off: switching libraries on comes with the new Word packs page.",
            LegacyLibraryPageContainment.Imported("Team", 3));
        Assert.StartsWith("Imported \"Team\" with 1 term.", LegacyLibraryPageContainment.Imported("Team", 1), StringComparison.Ordinal);
        Assert.StartsWith("\"Coding\" is built in and can't be removed.", LegacyLibraryPageContainment.BuiltInRemoveRefused("Coding"), StringComparison.Ordinal);
        Assert.StartsWith("Settings saved. ", LegacyLibraryPageContainment.SavedWithStoredSwitches, StringComparison.Ordinal);
        Assert.Contains("can't switch libraries on or off", LegacyLibraryPageContainment.SavedWithStoredSwitches, StringComparison.Ordinal);

        const string Note = LegacyLibraryPageContainment.CleanupLibrariesNote;
        Assert.Equal($"2 entries turned off. Review the change, then save to apply it. {Note}", LegacyLibraryPageContainment.CleanupResult(2, deleted: false));
        Assert.Equal($"1 entry removed. Review the change, then save to apply it. {Note}", LegacyLibraryPageContainment.CleanupResult(1, deleted: true));
        Assert.Equal(Note, LegacyLibraryPageContainment.CleanupResult(0, deleted: false));

        // The analyzer's summary for too little history advises turning libraries off on the Libraries page, which this
        // page can't; the message keeps the rest of it.
        var summary = DictionaryUsageAnalyzer.Analyze([], [], []).Summary;
        Assert.Contains("Libraries page", summary, StringComparison.Ordinal);
        var message = LegacyLibraryPageContainment.CleanupMessage(summary);
        Assert.DoesNotContain("Libraries page", message, StringComparison.Ordinal);
        Assert.StartsWith("Not enough dictation history yet", message, StringComparison.Ordinal);
        Assert.EndsWith(" " + Note, message, StringComparison.Ordinal);
        Assert.Equal($"Nothing to clean up. {Note}", LegacyLibraryPageContainment.CleanupMessage("Nothing to clean up."));

        // No dash of the kinds the repository keeps out of every text, in any of them.
        var constants = typeof(LegacyLibraryPageContainment)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .ToList();
        Assert.Equal(5, constants.Count);
        string[] built =
        [
            LegacyLibraryPageContainment.Imported("x", 2),
            LegacyLibraryPageContainment.BuiltInRemoveRefused("x"),
            LegacyLibraryPageContainment.CleanupResult(2, deleted: true),
            message,
        ];
        Assert.All(constants.Concat(built), text => Assert.DoesNotMatch("[\u2013\u2014]", text));
    }

    [Fact]
    public void The_old_settings_window_uses_the_containment()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));

        // The committed selection comes from the stored settings the window loads, before any page is built.
        const string Created = "_libraryContainment = new LegacyLibraryPageContainment(_settings.EnabledDictionaryLibraryIds);";
        Assert.Single(Regex.Matches(code, Regex.Escape(Created)));
        var load = code.IndexOf("_settings = settingsRepository.Load();", StringComparison.Ordinal);
        var created = code.IndexOf(Created, StringComparison.Ordinal);
        var grid = code.IndexOf("InitializeLibraryGrid();", StringComparison.Ordinal);
        Assert.True(load >= 0 && load < created && created < grid, "The containment is not created from the loaded settings.");

        // a. The On column is read-only, its box can't be toggled through UI Automation either, and the page says so.
        var contain = Body(code, "private void ContainLibrarySwitches()");
        Assert.Contains("ContainLibrarySwitches();", Body(code, "private void InitializeLibraryGrid()"), StringComparison.Ordinal);
        Assert.Contains(".IsReadOnly = true;", contain, StringComparison.Ordinal);
        Assert.Contains("new Setter(UIElement.IsEnabledProperty, false)", contain, StringComparison.Ordinal);
        Assert.Contains("LegacyLibraryPageContainment.PageNotice", contain, StringComparison.Ordinal);
        Assert.Contains("LegacyLibraryPageContainment.PageTip", contain, StringComparison.Ordinal);
        Assert.Contains("LegacyLibraryPageContainment.RemoveToolTip", contain, StringComparison.Ordinal);

        // b. The badges, the glossary count and the Save prompt judge the committed selection, and nothing judges the rows.
        foreach (var judgment in new[]
                 {
                     "DictionaryLibraryOverlapAnalyzer.Coverage(_loadedLibraries, _libraryContainment.CommittedIds)",
                     "LibraryPrecedence.Enabled(_loadedLibraries, _libraryContainment.CommittedIds)",
                     "DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries(entries, _loadedLibraries, _libraryContainment.CommittedIds)",
                 })
        {
            Assert.Contains(judgment, code, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(code, Regex.Escape(judgment[..(judgment.IndexOf('(') + 1)])));
        }

        Assert.DoesNotContain("EnabledLibraryRowIds", code, StringComparison.Ordinal);

        // c. The cleanup reviews no library, never switches one off or copies its terms, and says so.
        Assert.Single(Regex.Matches(code, Regex.Escape("DictionaryUsageAnalyzer.Analyze(")));
        Assert.Contains("DictionaryUsageAnalyzer.Analyze(transcripts, current, [])", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LibrarySwitchOffCopy", code, StringComparison.Ordinal);
        Assert.Contains("LegacyLibraryPageContainment.CleanupMessage(report.Summary)", code, StringComparison.Ordinal);
        Assert.Contains("LegacyLibraryPageContainment.CleanupResult(targets.Count, choice.Delete)", code, StringComparison.Ordinal);

        // d. Import and Remove say what is true now.
        Assert.Contains("LegacyLibraryPageContainment.Imported(imported.Name, imported.EnabledEntryCount)", code, StringComparison.Ordinal);
        Assert.Contains("LegacyLibraryPageContainment.BuiltInRemoveRefused(row.Name)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("then save to apply", code, StringComparison.Ordinal);
        Assert.DoesNotContain("with its checkbox", code, StringComparison.Ordinal);

        // e. The rows load as the committed selection has them. After the store, and only then, they show the stored list
        // the save handed back.
        Assert.Contains("_libraryRows.Add(NewLibraryRow(library, _libraryContainment.IsCommitted(library.Id)));", code, StringComparison.Ordinal);
        var save = code.IndexOf("private async Task<bool> TrySaveAsync()", StringComparison.Ordinal);
        var store = code.IndexOf("_settingsRepository.SaveBundle(", save, StringComparison.Ordinal);
        var apply = code.IndexOf("_applySettings(_settings);", store, StringComparison.Ordinal);
        var after = code.IndexOf(
            "_libraryRowsReset = _libraryContainment.AfterSave(", StringComparison.Ordinal);
        var reset = code.IndexOf("row.Enabled = _libraryContainment.IsCommitted(row.Id);", StringComparison.Ordinal);
        var failed = code.IndexOf("catch (Exception ex) when (_closed)", store, StringComparison.Ordinal);
        Assert.True(
            save >= 0 && save < store && store < apply && apply < after && after < reset && reset < failed,
            "The rows are not reset from the stored list right after the save that stored it.");
        Assert.Single(Regex.Matches(code, Regex.Escape("_libraryContainment.AfterSave(")));
        Assert.Contains(
            "_settings.EnabledDictionaryLibraryIds",
            code[after..code.IndexOf(';', after)],
            StringComparison.Ordinal);

        // Both Save buttons say a row that showed otherwise, in place of "Settings saved." or of closing.
        Assert.Single(Regex.Matches(code, Regex.Escape("ShowInfo(\"Settings saved.\");")));
        foreach (var (handler, otherwise) in new[]
                 {
                     ("private async void SaveButton_Click(", "ShowInfo(\"Settings saved.\");"),
                     ("private async void SaveCloseButton_Click(", "Close();"),
                 })
        {
            var body = Body(code, handler);
            var check = body.IndexOf("if (_libraryRowsReset)", StringComparison.Ordinal);
            var said = body.IndexOf("ShowInfo(LegacyLibraryPageContainment.SavedWithStoredSwitches", StringComparison.Ordinal);
            var otherwiseAt = body.IndexOf("else", said, StringComparison.Ordinal);
            Assert.True(
                check >= 0 && check < said && said < otherwiseAt && otherwiseAt < body.IndexOf(otherwise, otherwiseAt, StringComparison.Ordinal),
                $"{handler} does not say a reset row before it reports the save.");
        }
    }

    // A member's text, from its signature to the closing brace at its own indentation.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is missing.");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{signature} has no end.");
        return code[start..end];
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
