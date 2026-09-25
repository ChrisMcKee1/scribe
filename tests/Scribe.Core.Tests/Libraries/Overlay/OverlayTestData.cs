using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>Builders and comparisons the overlay's tests share. Shipped libraries are built directly, as the brief says.</summary>
internal static class OverlayTestData
{
    internal static BuiltInLibraryOverlay BuiltInOverlay => BuiltInLibraryOverlay.Instance;

    internal static TermValues T(string spoken, string written, bool wholeWord = true, bool enabled = true) =>
        new(spoken, written, wholeWord, enabled);

    internal static DictionaryLibrary Shipped(string id, params TermValues[] rows) =>
        new(id, "Test built-in", "Test", null, BuiltIn: true, [.. rows.Select(row => row.ToEntry())]);

    internal static DictionaryLibrary Shipped(params TermValues[] rows) => Shipped("github", rows);

    internal static LibraryTermKey K(string spoken) => LibraryTermKey.From(spoken);

    internal static BuiltInTermEdit Edited(string key, TermValues @base, TermValues value, TermValues? acknowledged = null) =>
        new(K(key), BuiltInTermIntent.Edited, @base, value, acknowledged);

    internal static BuiltInTermEdit Pinned(string key, TermValues @base, TermValues value, TermValues? acknowledged = null) =>
        new(K(key), BuiltInTermIntent.Pinned, @base, value, acknowledged);

    internal static BuiltInTermEdit Off(string key, TermValues @base) => new(K(key), BuiltInTermIntent.Off, @base, null);

    internal static BuiltInTermEdit Added(string key, TermValues value) => new(K(key), BuiltInTermIntent.Added, null, value);

    internal static BuiltInLibraryEdits Document(params BuiltInTermEdit[] terms) => new("github", terms);

    internal static LibraryRow Row(IReadOnlyList<LibraryRow> rows, string key) => rows.Single(row => row.Key == K(key));

    internal static IReadOnlyList<LibraryRow> Replace(IReadOnlyList<LibraryRow> rows, LibraryRow before, LibraryRow? after) =>
        after is null ? [.. rows.Where(row => !ReferenceEquals(row, before))] : [.. rows.Select(row => ReferenceEquals(row, before) ? after : row)];

    /// <summary>What a Save and the next load do to a built-in's rows: collect, write, read back, apply.</summary>
    internal static (BuiltInLibraryEdits? Document, IReadOnlyList<LibraryRow> Rows) SaveAndReload(
        DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows)
    {
        var collected = BuiltInOverlay.Collect(shipped, committed, rows);
        var stored = RoundTrip(collected, shipped.Id);
        return (stored, BuiltInOverlay.Apply(shipped, stored));
    }

    /// <summary>The document as the next load reads it back from the bytes the writer gives.</summary>
    internal static BuiltInLibraryEdits? RoundTrip(BuiltInLibraryEdits? edits, string libraryId = "github")
    {
        if (edits is null)
        {
            return null;
        }

        var read = BuiltInOverlay.ReadEdits(libraryId, BuiltInOverlay.WriteEdits(edits));
        Assert.Equal(LibraryFileState.Available, read.State);
        Assert.Equal(1, read.Version);
        return read.Edits;
    }

    internal static void AssertSameDocument(BuiltInLibraryEdits? expected, BuiltInLibraryEdits? actual, string? because = null)
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, Describe("one document is missing", because, expected, actual));
            return;
        }

        Assert.True(SameDocument(expected, actual), Describe("the documents differ", because, expected, actual));
    }

    internal static bool SameDocument(BuiltInLibraryEdits expected, BuiltInLibraryEdits actual) =>
        string.Equals(expected.LibraryId, actual.LibraryId, StringComparison.Ordinal) &&
        expected.Terms.Count == actual.Terms.Count &&
        expected.Terms.Zip(actual.Terms).All(pair => SameEntry(pair.First, pair.Second));

    internal static bool SameEntry(BuiltInTermEdit expected, BuiltInTermEdit actual) =>
        expected == actual && string.Equals(expected.Key.Value, actual.Key.Value, StringComparison.Ordinal);

    internal static void AssertSameRows(IReadOnlyList<LibraryRow> expected, IReadOnlyList<LibraryRow> actual, string? because = null)
    {
        var same = expected.Count == actual.Count && expected.Zip(actual).All(pair =>
            pair.First == pair.Second && string.Equals(pair.First.Key.Value, pair.Second.Key.Value, StringComparison.Ordinal));
        Assert.True(same, $"{because ?? "the rows differ"}\nexpected:\n{Describe(expected)}\nactual:\n{Describe(actual)}");
    }

    internal static string Describe(IReadOnlyList<LibraryRow> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append("  [").Append(row.Key.Value).Append("] ").Append(row.Origin).Append(' ').Append(Describe(row.Values))
                .Append(" shipped=").Append(Describe(row.Shipped))
                .Append(" intent=").Append(row.Edit?.Intent.ToString() ?? "none")
                .Append(" review=").Append(row.Review is null ? "none" : row.Review.Differing.ToString())
                .AppendLine();
        }

        return text.ToString();
    }

    internal static string Describe(TermValues? values) =>
        values is null ? "null" : $"({values.Spoken}|{values.Written}|{values.WholeWord}|{values.Enabled})";

    private static string Describe(BuiltInLibraryEdits? edits)
    {
        if (edits is null)
        {
            return "  (none)";
        }

        var text = new StringBuilder().Append("  library ").AppendLine(edits.LibraryId);
        foreach (var term in edits.Terms)
        {
            text.Append("  [").Append(term.Key.Value).Append("] ").Append(term.Intent)
                .Append(" base=").Append(Describe(term.Base))
                .Append(" value=").Append(Describe(term.Value))
                .Append(" acknowledged=").Append(Describe(term.Acknowledged))
                .AppendLine();
        }

        return text.ToString();
    }

    private static string Describe(string what, string? because, BuiltInLibraryEdits? expected, BuiltInLibraryEdits? actual) =>
        $"{because ?? what}\nexpected:\n{Describe(expected)}\nactual:\n{Describe(actual)}";
}
