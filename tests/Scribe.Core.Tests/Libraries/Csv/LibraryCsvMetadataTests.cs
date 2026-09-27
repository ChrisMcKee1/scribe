using System.Text;
using System.Text.Json;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// Library metadata through both of the codec's encodings (review finding A11, acceptance X-10), on the shared fixtures
/// in <c>tests/fixtures/libraries/csv/metadata</c>: a managed file keeps 0.4.3's raw comment lines, which this version
/// and 0.4.3 read back alike and which a managed read never strips; an export quotes each metadata line as one CSV
/// field, so a spreadsheet's padding and re-quoting cannot change a value.
/// </summary>
/// <remarks>
/// The expected files are pinned. After a deliberate change to what the codec writes, set
/// <c>SCRIBE_WRITE_CSV_FIXTURES=1</c>, run these tests and <see cref="LibraryCsvFixtureTests"/>, and review the diff: the
/// macOS port checks itself against these files.
/// </remarks>
public sealed class LibraryCsvMetadataTests
{
    private static readonly LibraryCsvCodec Codec = CsvTestData.Codec;

    public static TheoryData<string> Labels()
    {
        var data = new TheoryData<string>();
        foreach (var item in Fixture().GetProperty("cases").EnumerateArray())
        {
            data.Add(item.GetProperty("label").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void Metadata_reads_back_from_a_managed_file_here_and_in_0_4_3(string label)
    {
        var content = Case(label);

        var bytes = Pinned($"{label}.managed.csv", Codec.WriteManaged(content));
        var read = Codec.ReadManaged(bytes);
        var legacy = Legacy043LibraryCsv.Parse(CsvTestData.ReadAllTextOf(bytes));

        Assert.True(LibraryMetadata.ReadsBackInOlderVersions(content.Name, content.Category, content.Description, content.BasedOn));
        AssertMetadata(content, read.Name, read.Category, read.Description);
        Assert.Equal(content.BasedOn, read.BasedOn);
        Assert.Equal(content.Rows.Select(r => r.Values), read.Terms);
        Assert.Empty(read.Errors);
        AssertMetadata(content, legacy.Name, legacy.Category, legacy.Description);
        Assert.Equal(content.Rows.Select(r => r.Values), legacy.Entries.Select(TermValues.FromEntry));
        Assert.Empty(legacy.Errors);
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void Metadata_reads_back_from_an_export_and_from_that_export_after_a_spreadsheet_saved_it(string label)
    {
        var content = Case(label);

        var export = Pinned($"{label}.export.csv", Codec.WriteExport(content));
        var saved = Pinned(
            $"{label}.spreadsheet.csv",
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(CsvTestData.SpreadsheetSave(Encoding.UTF8.GetString(export.AsSpan(3))))]);

        foreach (var (bytes, which) in new[] { (export, "export"), (saved, "spreadsheet") })
        {
            var read = Codec.ReadImport(bytes);

            AssertMetadata(content, read.Name, read.Category, read.Description);
            Assert.True(content.BasedOn == read.BasedOn, which);
            Assert.Equal(content.Rows.Select(r => r.Values), read.Terms);
            Assert.Empty(read.Errors);
            Assert.Equal(1, read.FormulaGuardVersion);
        }

        Assert.Contains(",,,\r\n", Encoding.UTF8.GetString(saved), StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_whose_quotes_do_not_pair_is_refused_by_the_managed_writer_only()
    {
        var unpaired = Fixture().GetProperty("unpaired").EnumerateArray().ToList();
        Assert.NotEmpty(unpaired);
        foreach (var item in unpaired)
        {
            var content = CsvTestData.Content(
                item.GetProperty("name").GetString()!,
                item.GetProperty("category").GetString()!,
                item.GetProperty("description").GetString(),
                Rows());

            Assert.False(LibraryMetadata.ReadsBackInOlderVersions(content.Name, content.Category, content.Description));
            Assert.Throws<ArgumentException>(() => Codec.WriteManaged(content));
            var read = Codec.ReadImport(Codec.WriteExport(content));
            AssertMetadata(content, read.Name, read.Category, read.Description);
            Assert.Equal(content.Rows.Select(r => r.Values), read.Terms);
        }
    }

    [Fact]
    public void Round_1s_padding_recovery_would_have_eaten_the_trailing_comma()
    {
        // The A11 counterexample as a direct check: a managed read that dropped trailing commas from a metadata line, as
        // round 1 specified, gives "Team" for a library 0.4.3 reads as "Team,". This read keeps it.
        var bytes = Codec.WriteManaged(Case("trailing-comma"));

        Assert.Equal("Team,", Codec.ReadManaged(bytes).Name);
        Assert.Equal("Team,", Legacy043LibraryCsv.Parse(CsvTestData.Text(bytes)).Name);
    }

    private static void AssertMetadata(LibraryContent content, string? name, string? category, string? description)
    {
        Assert.Equal(content.Name, name);
        Assert.Equal(content.Category, category);
        Assert.Equal(content.Description, description);
    }

    private static LibraryContent Case(string label)
    {
        var item = Fixture().GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("label").GetString() == label);
        return CsvTestData.Content(
            item.GetProperty("name").GetString()!,
            item.GetProperty("category").GetString()!,
            item.GetProperty("description").GetString(),
            Rows(),
            item.GetProperty("basedOn").GetString());
    }

    private static List<TermValues> Rows() =>
        Fixture().GetProperty("rows").EnumerateArray().Select(row => new TermValues(
            row.GetProperty("spoken").GetString()!,
            row.GetProperty("written").GetString()!,
            row.GetProperty("wholeWord").GetBoolean(),
            row.GetProperty("enabled").GetBoolean())).ToList();

    private static JsonElement Fixture() =>
        JsonDocument.Parse(File.ReadAllText(CsvTestData.FixturePath("metadata", "cases.json"))).RootElement;

    // The committed file must hold exactly what the codec writes; SCRIBE_WRITE_CSV_FIXTURES=1 rewrites it instead.
    private static byte[] Pinned(string fileName, byte[] actual)
    {
        var path = CsvTestData.FixturePath("metadata", fileName);
        if (LibraryCsvFixtureTests.Regenerating)
        {
            File.WriteAllBytes(path, actual);
        }

        Assert.True(File.Exists(path), $"{fileName} is missing; set {LibraryCsvFixtureTests.UpdateVariable}=1 and run the tests once.");
        Assert.True(
            File.ReadAllBytes(path).AsSpan().SequenceEqual(actual),
            $"{fileName} differs from what the codec writes; if the change is meant, set {LibraryCsvFixtureTests.UpdateVariable}=1.");
        return actual;
    }
}
