using Microsoft.Extensions.Logging.Abstractions;

using Scribe.Core.Models;

using Scribe.Core.Persistence;



namespace Scribe.Core.Tests;



public sealed class HistoryRepositoryReadTests : IDisposable

{

    private readonly TempDatabaseFolder _folder = new();



    public void Dispose() => _folder.Dispose();



    [Fact]

    public void Older_page_uses_timestamp_and_id_boundary_without_repeating_rows()

    {

        using var database = _folder.Open();

        var repository = new HistoryRepository(database);

        var sameTime = DateTimeOffset.Parse("2026-09-26T10:00:00Z");

        var first = repository.Add(Entry(sameTime, "first at the tied time"));

        var second = repository.Add(Entry(sameTime, "second at the tied time"));

        var newest = repository.Add(Entry(sameTime.AddMinutes(1), "newest"));

        var oldest = repository.Add(Entry(sameTime.AddMinutes(-1), "oldest"));



        var firstPage = repository.GetRecent(2);



        Assert.Equal([newest.Id, second.Id], firstPage.Select(entry => entry.Id));

        var older = repository.GetOlder(second.TimestampUtc, second.Id, 10);

        Assert.Equal([first.Id, oldest.Id], older.Select(entry => entry.Id));

        Assert.DoesNotContain(older, entry => entry.Id == second.Id);

        Assert.Empty(repository.GetOlder(oldest.TimestampUtc, oldest.Id, 10));

    }



    [Fact]

    public void Search_treats_like_wildcards_as_literal_text()

    {

        using var database = _folder.Open();

        var repository = new HistoryRepository(database);

        var percent = repository.Add(Entry("the job is 100% done"));

        repository.Add(Entry("the job is 100 percent done"));

        var underscore = repository.Add(Entry("ticket ABC_123 closed"));

        repository.Add(Entry("ticket ABCX123 closed"));



        Assert.Equal([percent.Id], repository.Search("100%", 10).Select(entry => entry.Id));

        Assert.Equal([underscore.Id], repository.Search("ABC_123", 10).Select(entry => entry.Id));

    }



    [Fact]

    public void Search_uses_sqlite_like_for_text_and_friendly_app_names_for_apps()

    {

        using var database = _folder.Open();

        var repository = new HistoryRepository(database);

        var ascii = repository.Add(Entry("ASCII case match"));

        var accentNullApp = repository.Add(Entry("Été forecast"));

        var accentWithApp = repository.Add(Entry("Été plan", targetApp: "notepad"));

        var app = repository.Add(Entry("plain text", targetApp: "notepad"));



        var accentResults = repository.Search("été", 10);

        Assert.DoesNotContain(accentResults, entry => entry.Id == accentNullApp.Id);

        Assert.DoesNotContain(accentResults, entry => entry.Id == accentWithApp.Id);

        Assert.Contains(repository.Search("ascii", 10), entry => entry.Id == ascii.Id);

        Assert.Contains(repository.Search("Notepad", 10), entry => entry.Id == app.Id);

    }



    [Fact]

    public void Search_is_newest_first_and_capped()

    {

        using var database = _folder.Open();

        var repository = new HistoryRepository(database);

        var oldest = repository.Add(Entry(Now.AddMinutes(-2), "needle oldest"));

        var middle = repository.Add(Entry(Now.AddMinutes(-1), "needle middle"));

        var newest = repository.Add(Entry(Now, "needle newest"));



        var results = repository.Search("needle", 2);



        Assert.Equal([newest.Id, middle.Id], results.Select(entry => entry.Id));

        Assert.DoesNotContain(results, entry => entry.Id == oldest.Id);

    }



    [Fact]

    public void Search_matches_friendly_app_names_after_rows_are_read()

    {

        using var database = _folder.Open();

        var repository = new HistoryRepository(database);

        var word = repository.Add(Entry("quarterly notes", targetApp: "WINWORD.EXE"));

        repository.Add(Entry("quarterly notes", targetApp: "notepad"));



        Assert.Equal([word.Id], repository.Search("Word", 10).Select(entry => entry.Id));

    }



    [Fact]

    public void Search_matches_unknown_app_names_but_not_known_process_names()

    {

        using var database = _folder.Open();

        var repository = new HistoryRepository(database);

        var unknown = repository.Add(Entry("plain text", targetApp: "customwriter.exe"));

        repository.Add(Entry("plain text", targetApp: "WINWORD.EXE"));



        Assert.Equal([unknown.Id], repository.Search("writer", 10).Select(entry => entry.Id));

        Assert.Empty(repository.Search("winword", 10));

    }



    [Fact]

    public void Ordered_search_waits_for_accepted_history_writes()

    {

        using var database = _folder.Open();

        var inner = new HistoryRepository(database);

        var writer = new CompletingWriter(() => inner.Add(Entry("needle from the accepted write")));

        var ordered = new OrderedHistoryRepository(

            inner,

            writer,

            NullLogger<OrderedHistoryRepository>.Instance,

            TimeSpan.FromSeconds(5),

            TimeSpan.FromSeconds(5));



        Assert.Contains(ordered.Search("accepted write", 10), entry => entry.Text == "needle from the accepted write");

        Assert.True(writer.Waited);

    }



    private sealed class CompletingWriter(Action complete) : IHistoryWriter

    {

        public bool Waited { get; private set; }



        public bool Enqueue(HistoryEntry entry, CapturedAudio? audio, long dictationId = 0) =>

            throw new NotSupportedException();



        public bool WaitForAcceptedWrites(TimeSpan timeout)

        {

            Waited = true;

            complete();

            return true;

        }



        public HistoryDrainResult Complete(TimeSpan timeout) => new(true, 0, 0);

    }



    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");



    private static HistoryEntry Entry(string text, string? targetApp = null) =>

        Entry(Now, text, targetApp);



    private static HistoryEntry Entry(DateTimeOffset timestampUtc, string text, string? targetApp = null) =>

        new(0, timestampUtc, text, AudioMilliseconds: 1000, DecodeMilliseconds: 100, TargetApp: targetApp);

}
