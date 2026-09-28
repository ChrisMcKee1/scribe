using System.Globalization;
using BenchmarkDotNet.Attributes;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Benchmarks;

/// <summary>
/// What SettingsWindow.RefreshFooterNow does for Your words on every keystroke, selection change, check toggle and row edit
/// anywhere in the Settings window (SettingsWindow.Footer.cs): clone the committed settings, project every dictionary row
/// into a DictionaryDraftRow and hand the lists to SettingsChangeTracker.Compare (with memopt UI-8's single pass). The rows
/// are saved and unchanged, the common case while the user types in any other field. <see cref="Current"/> reads each row's
/// key as the row type always made it (a new string per read); <see cref="CachedRowKeys"/> reads it through
/// <see cref="DraftRowKeyCache"/>, as LeanFooterRefresh does. LeanFooterRefresh also skips the whole refresh for typing in
/// the search boxes and Try dictation, selection in the navigation rail, the History list, the Dictionary tabs and the usage
/// period: for those events it saves all of <see cref="Current"/>.
/// </summary>
[MemoryDiagnoser]
public class SettingsFooterRefreshBenchmarks
{
    [Params(100, 1000, 5000)]
    public int Rows;

    private AppSettings _committed = null!;
    private List<RowModel> _rows = null!;
    private IReadOnlyList<LoadedDictionaryDraftRow> _loaded = null!;

    // SettingsWindow.DictionaryRow's shape, with both of its key paths.
    private sealed class RowModel
    {
        public long Id;
        public DraftRowOrigin Origin = DraftRowOrigin.Saved;
        public bool Touched { get; set; }
        public string? Pattern;
        public string? Replacement;
        public string? LoadedPattern;
        public string? LoadedReplacement;
        public bool WholeWord = true;
        public bool Enabled = true;
        public bool LoadedWholeWord = true;
        public bool LoadedEnabled = true;
        private DraftRowKeyCache _rowKey;

        public string RowKey => Id > 0 ? Id.ToString(CultureInfo.InvariantCulture) : $"new:{GetHashCode()}";

        public string CachedRowKey => _rowKey.ForId(Id, this);
    }

    [GlobalSetup]
    public void Setup()
    {
        _committed = AppSettings.CreateDefault();
        _rows = new List<RowModel>(Rows);
        for (var i = 1; i <= Rows; i++)
        {
            var pattern = "spoken form " + i.ToString(CultureInfo.InvariantCulture);
            var replacement = "Written Form " + i.ToString(CultureInfo.InvariantCulture);
            _rows.Add(new RowModel
            {
                Id = i,
                Pattern = pattern,
                Replacement = replacement,
                LoadedPattern = pattern,
                LoadedReplacement = replacement,
            });
        }

        _loaded = [.. _rows.Select(row => new LoadedDictionaryDraftRow(
            row.RowKey, row.LoadedPattern, row.LoadedReplacement, row.LoadedWholeWord, row.LoadedEnabled))];

        if (Current() != CachedRowKeys() || _rows.Any(row => row.RowKey != row.CachedRowKey))
        {
            throw new InvalidOperationException("The cached keys must answer exactly as the current ones do.");
        }
    }

    [Benchmark(Baseline = true)]
    public bool Current()
    {
        var draft = _committed.Clone();
        IReadOnlyList<DictionaryDraftRow> rows = [.. _rows.Select(row => new DictionaryDraftRow(
            row.RowKey,
            row.Origin,
            row.Touched,
            row.Pattern,
            row.Replacement,
            row.LoadedPattern,
            row.LoadedReplacement,
            row.WholeWord,
            row.Enabled,
            row.LoadedWholeWord,
            row.LoadedEnabled))];
        return SettingsChangeTracker.Compare(_committed, draft, rows, [], [], false, _loaded, [], []).IsDirty;
    }

    [Benchmark]
    public bool CachedRowKeys()
    {
        var draft = _committed.Clone();
        IReadOnlyList<DictionaryDraftRow> rows = [.. _rows.Select(row => new DictionaryDraftRow(
            row.CachedRowKey,
            row.Origin,
            row.Touched,
            row.Pattern,
            row.Replacement,
            row.LoadedPattern,
            row.LoadedReplacement,
            row.WholeWord,
            row.Enabled,
            row.LoadedWholeWord,
            row.LoadedEnabled))];
        return SettingsChangeTracker.Compare(_committed, draft, rows, [], [], false, _loaded, [], []).IsDirty;
    }
}
