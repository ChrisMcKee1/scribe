using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="SettingsChangeTracker.Compare"/> decides whether the dictionary, snippet and profile rows changed in one
/// in-order pass when the draft lists the loaded rows in their order, and by matching every row by key otherwise. The
/// footer asks on every keystroke and its answer enables Save, so these tests hold both paths to the comparison they
/// replaced, copied here: the same pages, or the same exception (type, message and parameter), for generated drafts that
/// line up, drift, repeat keys, lose rows, gain rows, carry placeholders and null keys or rows; and they show from the
/// allocations that a draft that lines up takes the in-order pass.
/// </summary>
public sealed class SettingsChangeTrackerInOrderTests
{
    private static readonly AppSettings Settings = AppSettings.CreateDefault();
    private static readonly string?[] Texts = ["a", "b", "Ab", "a ", null, "", " "];
    private static readonly string?[] Blanks = [null, "", " ", "\t"];
    private static readonly NewlineInjectionMode?[] Newlines = [null, NewlineInjectionMode.SmartFlatten, NewlineInjectionMode.AlwaysFlatten];

    [Fact]
    public void Generated_drafts_find_the_pages_or_the_exception_they_found()
    {
        var random = new Random(20260917);
        var mismatches = new List<string>();
        int linedUp = 0, drifted = 0, threw = 0;
        for (var scenario = 0; scenario < 30_000 && mismatches.Count < 10; scenario++)
        {
            var loaded = LoadedRows(random);
            var draft = DraftRows(random, loaded);
            var withoutRows = random.Next(20) == 0;
            var withoutLoaded = withoutRows && random.Next(2) == 0;
            var input = Input.Of(withoutRows ? null : draft, withoutLoaded ? null : loaded);

            var before = Outcome(() => Before(input));
            var after = Outcome(() => After(input));
            if (before != after)
            {
                mismatches.Add($"scenario {scenario}: before '{before}', after '{after}'");
            }

            if (before.StartsWith("threw", StringComparison.Ordinal))
            {
                threw++;
            }
            else if (LinesUp(input.DictionaryRows ?? [], input.LoadedDictionaryRows ?? []))
            {
                linedUp++;
            }
            else
            {
                drifted++;
            }
        }

        Assert.Empty(mismatches);
        Assert.True(linedUp > 5_000 && drifted > 5_000 && threw > 500, $"lined up {linedUp}, drifted {drifted}, threw {threw}");
    }

    [Fact]
    public void A_draft_that_lines_up_is_changed_exactly_when_a_row_differs()
    {
        Row[] loaded = [Saved("1"), Saved("2"), Saved("3")];
        Assert.Equal("dictionary False, snippets False, profiles False", Outcome(() => After(Input.Of(loaded, loaded))));

        for (var position = 0; position < loaded.Length; position++)
        {
            foreach (var edit in new Func<Row, Row>[]
            {
                row => row with { First = "changed" },
                row => row with { Second = "changed" },
                row => row with { Third = "changed" },
                row => row with { One = !row.One },
                row => row with { Two = !row.Two },
                row => row with { Newline = NewlineInjectionMode.AlwaysFlatten },
            })
            {
                var draft = loaded.ToArray();
                draft[position] = edit(draft[position]);
                var input = Input.Of(draft, loaded);
                Assert.Equal(Outcome(() => Before(input)), Outcome(() => After(input)));
            }
        }
    }

    [Fact]
    public void Placeholders_anywhere_in_a_draft_that_lines_up_are_left_out()
    {
        Row[] loaded = [Saved("1"), Saved("2")];
        Row?[] draft = [Placeholder("new:1"), loaded[0], Placeholder("1"), Placeholder("new:2"), loaded[1], Placeholder("2")];

        Assert.Equal("dictionary False, snippets False, profiles False", Outcome(() => After(Input.Of(draft, loaded))));
    }

    [Fact]
    public void A_repeated_loaded_key_still_throws_what_it_threw_even_when_the_draft_lines_up()
    {
        Row[] loaded = [Saved("1"), Saved("2"), Saved("1")];
        var input = Input.Of(loaded, loaded);

        var before = Outcome(() => Before(input));
        Assert.StartsWith("threw System.ArgumentException", before, StringComparison.Ordinal);
        Assert.Equal(before, Outcome(() => After(input)));
    }

    [Fact]
    public void A_changed_row_before_a_repeated_draft_key_still_throws_what_it_threw()
    {
        Row[] loaded = [Saved("1"), Saved("2"), Saved("3")];
        Row[] draft = [loaded[0] with { First = "changed" }, loaded[1], loaded[1]];
        var input = Input.Of(draft, loaded);

        var before = Outcome(() => Before(input));
        Assert.StartsWith("threw System.ArgumentException", before, StringComparison.Ordinal);
        Assert.Equal(before, Outcome(() => After(input)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void Null_keys_and_null_rows_throw_what_they_threw(bool nullLoadedKey, bool nullDraftKey, bool nullDraftRow)
    {
        Row[] loaded = [Saved("1"), Saved(nullLoadedKey ? null : "2"), Saved("3"), Saved("3")];
        Row?[] draft = [loaded[0], loaded[1] with { Key = nullDraftKey ? null : loaded[1].Key }, nullDraftRow ? null : loaded[2], loaded[3]];
        var input = Input.Of(draft, loaded);

        var before = Outcome(() => Before(input));
        Assert.StartsWith("threw", before, StringComparison.Ordinal);
        Assert.Equal(before, Outcome(() => After(input)));
    }

    private static List<Row?> LoadedRows(Random random)
    {
        var rows = new List<Row?>();
        var count = random.Next(9);
        for (var i = 0; i < count; i++)
        {
            string? key = random.Next(100) switch
            {
                < 3 => null,
                < 10 when rows.Count > 0 => rows[random.Next(rows.Count)]?.Key,
                _ => $"k{i}",
            };
            rows.Add(random.Next(150) == 0 ? null : new Row(
                key,
                DraftRowOrigin.Saved,
                Pick(random, Texts),
                Pick(random, Texts),
                Pick(random, Texts),
                random.Next(2) == 0,
                random.Next(2) == 0,
                Pick(random, Newlines)));
        }

        return rows;
    }

    private static List<Row?> DraftRows(Random random, List<Row?> loaded)
    {
        var rows = new List<Row?>(loaded);
        var edits = random.Next(100) switch
        {
            < 35 => 0,
            < 75 => 1,
            _ => random.Next(2, 5),
        };
        for (var edit = 0; edit < edits; edit++)
        {
            var at = rows.Count == 0 ? 0 : random.Next(rows.Count);
            var row = rows.Count == 0 ? null : rows[at];
            switch (random.Next(10))
            {
                case 0 or 1 or 2 when row is not null:
                    rows[at] = random.Next(6) switch
                    {
                        0 => row with { First = Pick(random, Texts) },
                        1 => row with { Second = Pick(random, Texts) },
                        2 => row with { Third = Pick(random, Texts) },
                        3 => row with { One = !row.One },
                        4 => row with { Two = !row.Two },
                        _ => row with { Newline = Pick(random, Newlines) },
                    };
                    break;
                case 3 when rows.Count > 1:
                    var other = random.Next(rows.Count);
                    (rows[at], rows[other]) = (rows[other], rows[at]);
                    break;
                case 4 when rows.Count > 0:
                    rows.RemoveAt(at);
                    break;
                case 5:
                    rows.Insert(random.Next(rows.Count + 1), Placeholder(random.Next(3) == 0 ? row?.Key : $"new:{random.Next(50)}", random));
                    break;
                case 6:
                    var key = random.Next(3) == 0 ? row?.Key : $"new:{random.Next(50)}";
                    rows.Insert(random.Next(rows.Count + 1), new Row(key, DraftRowOrigin.New, "added", Pick(random, Texts), null, true, true, null));
                    break;
                case 7 when row is not null:
                    rows[at] = row with { Key = random.Next(3) switch { 0 => null, 1 => rows[random.Next(rows.Count)]?.Key, _ => "k99" } };
                    break;
                case 8 when row is not null:
                    rows[at] = row with { Origin = DraftRowOrigin.New, First = Pick(random, Blanks), Second = Pick(random, Blanks) };
                    break;
                case 9 when random.Next(20) == 0:
                    rows.Insert(random.Next(rows.Count + 1), null);
                    break;
            }
        }

        return rows;
    }

    // Whether the dictionary rows take the in-order pass: no null row, the draft's rows that are not placeholders have the
    // loaded rows' keys in their order, and the loaded keys are distinct and not null.
    private static bool LinesUp(IReadOnlyList<DictionaryDraftRow> rows, IReadOnlyList<LoadedDictionaryDraftRow> loaded)
    {
        if (rows.Any(row => row is null) || loaded.Any(row => row is null || row.RowKey is null))
        {
            return false;
        }

        var keys = rows.Where(row => !SettingsDraftValidator.IsPlaceholder(row)).Select(row => row.RowKey).ToArray();
        return keys.SequenceEqual(loaded.Select(row => row.RowKey), StringComparer.Ordinal)
            && loaded.Select(row => row.RowKey).Distinct(StringComparer.Ordinal).Count() == loaded.Count;
    }

    private static string Outcome(Func<(bool Dictionary, bool Snippets, bool Profiles)> compare)
    {
        try
        {
            var (dictionary, snippets, profiles) = compare();
            return $"dictionary {dictionary}, snippets {snippets}, profiles {profiles}";
        }
        catch (Exception failure) when (failure is ArgumentException or NullReferenceException)
        {
            return $"threw {failure.GetType().FullName}: {failure.Message} ({(failure as ArgumentException)?.ParamName})";
        }
    }

    private static (bool Dictionary, bool Snippets, bool Profiles) After(Input input)
    {
        var pages = SettingsChangeTracker.Compare(
            Settings,
            Settings,
            input.DictionaryRows,
            input.SnippetRows,
            input.ProfileRows,
            recoveredMode: false,
            input.LoadedDictionaryRows,
            input.LoadedSnippetRows,
            input.LoadedProfileRows).Pages;
        return (pages.Contains(SettingsPage.Dictionary), pages.Contains(SettingsPage.VoiceSnippets), pages.Contains(SettingsPage.AppProfiles));
    }

    // In the order Compare asks: the dictionary, then the snippets, then the profiles.
    private static (bool Dictionary, bool Snippets, bool Profiles) Before(Input input) =>
        (BeforeDictionaryRowsChanged(input.DictionaryRows, input.LoadedDictionaryRows),
            BeforeSnippetRowsChanged(input.SnippetRows, input.LoadedSnippetRows),
            BeforeProfileRowsChanged(input.ProfileRows, input.LoadedProfileRows));

    // SettingsChangeTracker's row comparisons of 10c9a0b, which matched every row by key through two dictionaries.
    private static bool BeforeDictionaryRowsChanged(
        IReadOnlyList<DictionaryDraftRow>? rows,
        IReadOnlyList<LoadedDictionaryDraftRow>? loadedRows)
    {
        var loaded = (loadedRows ?? [])
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var draft = (rows ?? [])
            .Where(row => !SettingsDraftValidator.IsPlaceholder(row))
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        if (!SameKeys(loaded.Keys, draft.Keys))
        {
            return true;
        }

        foreach (var (key, before) in loaded)
        {
            var after = draft[key];
            if (!Same(before.Pattern, after.Pattern) ||
                !Same(before.Replacement, after.Replacement) ||
                before.WholeWord != after.WholeWord ||
                before.Enabled != after.Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool BeforeSnippetRowsChanged(
        IReadOnlyList<SnippetDraftRow>? rows,
        IReadOnlyList<LoadedSnippetDraftRow>? loadedRows)
    {
        var loaded = (loadedRows ?? [])
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var draft = (rows ?? [])
            .Where(row => !SettingsDraftValidator.IsPlaceholder(row))
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        if (!SameKeys(loaded.Keys, draft.Keys))
        {
            return true;
        }

        foreach (var (key, before) in loaded)
        {
            var after = draft[key];
            if (!Same(before.Phrase, after.Phrase) ||
                !Same(before.Template, after.Template) ||
                before.Enabled != after.Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool BeforeProfileRowsChanged(
        IReadOnlyList<ProfileDraftRow>? rows,
        IReadOnlyList<LoadedProfileDraftRow>? loadedRows)
    {
        var loaded = (loadedRows ?? [])
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var draft = (rows ?? [])
            .Where(row => !SettingsDraftValidator.IsPlaceholder(row))
            .ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        if (!SameKeys(loaded.Keys, draft.Keys))
        {
            return true;
        }

        foreach (var (key, before) in loaded)
        {
            var after = draft[key];
            if (!Same(before.Name, after.Name) ||
                !Same(before.Apps, after.Apps) ||
                !Same(before.WritingStyle, after.WritingStyle) ||
                before.NewlineHandling != after.NewlineHandling)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool SameKeys(IEnumerable<string> left, IEnumerable<string> right) =>
        new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);

    private static T Pick<T>(Random random, T[] values) => values[random.Next(values.Length)];

    private static Row Saved(string? key) => new(key, DraftRowOrigin.Saved, "said", "written", "style", true, true, null);

    private static Row Placeholder(string? key) => new(key, DraftRowOrigin.New, null, " ", "", false, true, null);

    private static Row Placeholder(string? key, Random random) =>
        new(key, DraftRowOrigin.New, Pick(random, Blanks), Pick(random, Blanks), Pick(random, Blanks), random.Next(2) == 0, random.Next(2) == 0, null);

    // One generated row, read as a row of each kind: First and Second are the spoken and written forms, the phrase and
    // template, or the name and apps; Third is a profile's writing style; One and Two are the flags; Newline a profile's.
    private sealed record Row(
        string? Key,
        DraftRowOrigin Origin,
        string? First,
        string? Second,
        string? Third,
        bool One,
        bool Two,
        NewlineInjectionMode? Newline);

    private sealed record Input(
        IReadOnlyList<DictionaryDraftRow>? DictionaryRows,
        IReadOnlyList<LoadedDictionaryDraftRow>? LoadedDictionaryRows,
        IReadOnlyList<SnippetDraftRow>? SnippetRows,
        IReadOnlyList<LoadedSnippetDraftRow>? LoadedSnippetRows,
        IReadOnlyList<ProfileDraftRow>? ProfileRows,
        IReadOnlyList<LoadedProfileDraftRow>? LoadedProfileRows)
    {
        public static Input Of(IReadOnlyList<Row?>? draft, IReadOnlyList<Row?>? loaded) => new(
            draft?.Select(row => row is null ? null! : new DictionaryDraftRow(
                row.Key!, row.Origin, Touched: row.Two, Pattern: row.First, Replacement: row.Second, WholeWord: row.One, Enabled: row.Two)).ToArray(),
            loaded?.Select(row => row is null ? null! : new LoadedDictionaryDraftRow(row.Key!, row.First, row.Second, row.One, row.Two)).ToArray(),
            draft?.Select(row => row is null ? null! : new SnippetDraftRow(
                row.Key!, row.Origin, Touched: row.One, Phrase: row.First, Template: row.Second, Enabled: row.One)).ToArray(),
            loaded?.Select(row => row is null ? null! : new LoadedSnippetDraftRow(row.Key!, row.First, row.Second, row.One)).ToArray(),
            draft?.Select(row => row is null ? null! : new ProfileDraftRow(
                row.Key!, row.Origin, Touched: row.One, Name: row.First, Apps: row.Second, WritingStyle: row.Third, NewlineHandling: row.Newline)).ToArray(),
            loaded?.Select(row => row is null ? null! : new LoadedProfileDraftRow(row.Key!, row.First, row.Second, row.Third, row.Newline)).ToArray());
    }

    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void A_thousand_rows_that_line_up_are_compared_without_the_match_by_key()
        {
            var loaded = Enumerable.Range(1, 1_000).Select(i => Saved(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var linedUp = Input.Of(loaded, loaded);
            var swapped = loaded.ToArray();
            (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
            var drifted = Input.Of(swapped, loaded);
            for (var i = 0; i < 5; i++)
            {
                _ = After(linedUp);
                _ = After(drifted);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var inOrder = After(linedUp);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            before = GC.GetAllocatedBytesForCurrentThread();
            var byKey = After(drifted);
            var matchedByKey = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal((false, false, false), inOrder);
            Assert.Equal((false, false, false), byKey);

            // A bound, not an exact size: three sets of the 1,000 loaded keys (about 22 KB each, one per row kind) and the
            // change set. The match by key builds two dictionaries and a key set for each kind on top of that.
            Assert.True(allocated <= 80 * 1024, $"Lined up: {allocated} bytes. During it: {during}.");
            Assert.True(matchedByKey > 2 * allocated, $"Lined up: {allocated} bytes; matched by key: {matchedByKey} bytes.");
        }
    }
}
