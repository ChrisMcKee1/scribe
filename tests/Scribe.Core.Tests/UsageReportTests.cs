using Scribe.Core.Diagnostics;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class UsageReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Period_counts_only_the_selected_days_including_today()
    {
        var history = new[]
        {
            Entry(1, Now.AddHours(-1), "today"),
            Entry(2, Now.AddDays(-6).AddHours(1), "six days ago"),
            Entry(3, Now.AddDays(-10), "ten days ago"),
        };

        var week = Build(history, periodDays: 7);
        var all = Build(history, periodDays: null);

        Assert.Equal(2, week.Snapshot.Dictations);
        Assert.False(week.PeriodCapped);
        Assert.Equal(3, all.Snapshot.Dictations);
        Assert.False(all.PeriodCapped);
    }

    [Fact]
    public void Coverage_uses_both_the_saved_dictionary_and_the_enabled_libraries()
    {
        var history = new[]
        {
            Entry(1, Now.AddHours(-1), "Deployed Kubernetes with Terraform."),
            Entry(2, Now.AddHours(-2), "Kubernetes and Terraform again."),
        };

        var result = UsageReport.Build(
            limit => history,
            () => [DictionaryEntry.New("kubernetes", "Kubernetes")],
            () => [DictionaryEntry.New("terraform", "Terraform")],
            periodDays: 7,
            Now,
            CancellationToken.None);

        Assert.Contains(result.Snapshot.Terms, term => term is { Text: "Kubernetes", Covered: true });
        Assert.Contains(result.Snapshot.Terms, term => term is { Text: "Terraform", Covered: true });
    }

    [Fact]
    public void Reads_one_row_past_the_cap_and_reports_a_capped_period()
    {
        var requested = 0;
        var history = Enumerable.Range(0, UsageReport.HistoryLimit + 1)
            .Select(i => Entry(i + 1, Now.AddMinutes(-i), "word"))
            .ToList();

        var result = UsageReport.Build(
            limit => { requested = limit; return history; },
            () => [],
            () => [],
            periodDays: 7,
            Now,
            CancellationToken.None);

        Assert.Equal(UsageReport.HistoryLimit + 1, requested);
        Assert.True(result.PeriodCapped);
        Assert.Equal(UsageReport.HistoryLimit, result.Snapshot.Dictations);
    }

    [Fact]
    public void Extra_row_outside_the_period_does_not_count_as_capped()
    {
        var history = Enumerable.Range(0, UsageReport.HistoryLimit)
            .Select(i => Entry(i + 1, Now.AddSeconds(-i), "word"))
            .Append(Entry(UsageReport.HistoryLimit + 1, Now.AddDays(-30), "old"))
            .ToList();

        Assert.False(Build(history, periodDays: 7).PeriodCapped);
        Assert.True(Build(history, periodDays: null).PeriodCapped);
    }

    [Fact]
    public void Cancellation_is_honored_between_steps_it_cannot_interrupt()
    {
        using var cancellation = new CancellationTokenSource();
        var dictionaryRead = false;

        // The history read itself cannot be interrupted; a request superseding it while it runs is
        // noticed at the next checkpoint, before any further database work.
        Assert.Throws<OperationCanceledException>(() => UsageReport.Build(
            limit => { cancellation.Cancel(); return [Entry(1, Now, "text")]; },
            () => { dictionaryRead = true; return []; },
            () => [],
            periodDays: 7,
            Now,
            cancellation.Token));

        Assert.False(dictionaryRead);
    }

    [Fact]
    public void Already_cancelled_request_does_no_database_work()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var historyRead = false;

        Assert.Throws<OperationCanceledException>(() => UsageReport.Build(
            limit => { historyRead = true; return []; },
            () => [],
            () => [],
            periodDays: null,
            Now,
            cancellation.Token));

        Assert.False(historyRead);
    }

    private static UsageReport.Result Build(IReadOnlyList<HistoryEntry> history, int? periodDays) =>
        UsageReport.Build(limit => history, () => [], () => [], periodDays, Now, CancellationToken.None);

    private static HistoryEntry Entry(long id, DateTimeOffset when, string text) =>
        new(id, when, text, AudioMilliseconds: 1000, DecodeMilliseconds: 100, TargetApp: "notepad");
}
