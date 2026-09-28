using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// BoundedDiagnosticsReads: the Diagnostics page reads the 20 failures it shows and a count up to 10,000 instead of 10,000
/// failures. The page must hold exactly what the old read gave the page (its first rows, in order, and its length).
/// </summary>
public sealed class CleanupFailureLogPageTests
{
    [Theory]
    [InlineData(0, 20, 10_000)]
    [InlineData(1, 20, 10_000)]
    [InlineData(19, 20, 10_000)]
    [InlineData(20, 20, 10_000)]
    [InlineData(21, 20, 10_000)]
    [InlineData(45, 20, 30)]
    [InlineData(45, 40, 30)]
    [InlineData(12, 0, 10)]
    [InlineData(12, -3, 10)]
    [InlineData(12, 5, 0)]
    [InlineData(12, 5, -1)]
    public void The_page_is_what_the_old_read_gave_the_page(int stored, int shown, int countCap)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var log = new CleanupFailureLog(db);
        var start = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < stored; i++)
        {
            // Every third failure shares its timestamp with the one before, so the id decides their order.
            var at = start.AddMinutes(i - (i % 3 == 2 ? 1 : 0));
            log.Add(new CleanupFailure(0, at, i % 2 == 0 ? "MicrosoftFoundry" : null, i % 4 == 0 ? null : "gpt-6-astra", $"reason {i}", i % 5 == 0 ? null : $"sample {i}"));
        }

        var old = log.GetRecent(countCap);
        var page = log.GetRecentPage(shown, countCap);

        Assert.Equal(old.Take(Math.Max(0, shown)).ToList(), page.Recent);
        Assert.Equal(old.Count, page.Count);
    }

    [Fact]
    public void An_implementation_without_its_own_page_gets_the_old_read_through_the_default()
    {
        ICleanupFailureLog fake = new FakeLog(Enumerable.Range(1, 30).Select(i =>
            new CleanupFailure(i, DateTimeOffset.UnixEpoch.AddMinutes(-i), null, null, $"reason {i}", null)).ToList());

        var page = fake.GetRecentPage(20, 25);

        Assert.Equal(20, page.Recent.Count);
        Assert.Equal(25, page.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), page.Recent.Select(f => f.Id));
    }

    private sealed class FakeLog(IReadOnlyList<CleanupFailure> newestFirst) : ICleanupFailureLog
    {
        public CleanupFailure Add(CleanupFailure failure) => throw new NotSupportedException();

        public IReadOnlyList<CleanupFailure> GetRecent(int limit = 50) => [.. newestFirst.Take(Math.Max(0, limit))];

        public int Count() => newestFirst.Count;

        public int Clear() => throw new NotSupportedException();

        public int PruneOlderThan(DateTimeOffset cutoffUtc) => throw new NotSupportedException();
    }
}
