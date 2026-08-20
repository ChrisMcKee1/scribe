using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class ScribeLogFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "scribe-logs-test-" + Guid.NewGuid().ToString("N"));

    public ScribeLogFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteLog(DateOnly day, int bytes = 16)
    {
        var path = ScribeLogFiles.PathFor(_dir, day);
        File.WriteAllText(path, new string('x', bytes));
        return path;
    }

    [Fact]
    public void File_names_round_trip_through_the_convention()
    {
        var day = new DateOnly(2026, 8, 20);

        Assert.Equal("scribe-20260820.log", ScribeLogFiles.FileNameFor(day));
        Assert.True(ScribeLogFiles.TryParseDay("scribe-20260820.log", out var parsed));
        Assert.Equal(day, parsed);
    }

    [Theory]
    [InlineData("keyobserver.log")]
    [InlineData("scribe.log")]
    [InlineData("scribe-notadate.log")]
    [InlineData("scribe-20260820.txt")]
    [InlineData("")]
    public void Names_outside_the_convention_are_not_recognised(string name)
    {
        // The sweep deletes what this parser claims. A parser that accepts a stray file is a parser
        // that deletes somebody else's data out of a shared folder.
        Assert.False(ScribeLogFiles.TryParseDay(name, out _));
    }

    [Fact]
    public void Enumerate_ignores_files_it_did_not_write()
    {
        WriteLog(new DateOnly(2026, 8, 20));
        File.WriteAllText(Path.Combine(_dir, "keyobserver.log"), "unrelated");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "unrelated");

        var found = ScribeLogFiles.Enumerate(_dir);

        Assert.Single(found);
        Assert.Equal(new DateOnly(2026, 8, 20), found[0].Day);
    }

    [Fact]
    public void Enumerate_on_a_missing_folder_is_empty_rather_than_throwing()
    {
        Assert.Empty(ScribeLogFiles.Enumerate(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void Prune_deletes_only_the_days_outside_the_window()
    {
        var today = new DateOnly(2026, 8, 20);
        var kept = WriteLog(today);
        var alsoKept = WriteLog(today.AddDays(-6));
        var expired = WriteLog(today.AddDays(-7));
        var ancient = WriteLog(today.AddDays(-90));
        var unrelated = Path.Combine(_dir, "keyobserver.log");
        File.WriteAllText(unrelated, "unrelated");

        var deleted = ScribeLogFiles.Prune(_dir, today);

        Assert.Equal(2, deleted);
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(alsoKept));
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(ancient));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Prune_leaves_a_file_another_process_holds_open()
    {
        // Both Scribe processes append to these files, and a user may have one open in a viewer.
        // A locked file must cost the sweep nothing: it is retried on the next rollover.
        var today = new DateOnly(2026, 8, 20);
        var locked = WriteLog(today.AddDays(-30));
        var deletable = WriteLog(today.AddDays(-31));

        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var deleted = ScribeLogFiles.Prune(_dir, today);
            Assert.Equal(1, deleted);
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(deletable));
    }
}
