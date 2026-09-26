using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public sealed class HistoryReadGenerationTests
{
    [Fact]
    public void Search_started_before_a_delete_cannot_publish()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        generation.AdvanceForDeletion();

        Assert.False(generation.IsCurrent(ticket));
    }

    [Fact]
    public void Search_started_before_a_completed_rating_cannot_publish()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        generation.CompleteRatingWrite(saved: true);

        Assert.False(generation.IsCurrent(ticket));
    }

    [Fact]
    public void Failed_rating_also_rejects_an_earlier_read()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        generation.CompleteRatingWrite(saved: false);

        Assert.False(generation.IsCurrent(ticket));
    }

    [Fact]
    public void Current_search_retries_when_a_mutation_made_its_read_stale()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();
        generation.AdvanceForDeletion();

        Assert.Equal(HistoryReadCompletion.Retry, generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: true));
    }

    [Fact]
    public void Needed_first_load_retries_when_a_mutation_made_its_read_stale()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();
        generation.AdvanceForDeletion();

        Assert.Equal(HistoryReadCompletion.Retry, generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: true));
    }

    [Fact]
    public void Superseded_older_page_drops_instead_of_appending()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        Assert.Equal(HistoryReadCompletion.Drop, generation.CompleteRead(ticket, requestStillCurrent: false, retryWhenStale: false));
    }

    [Fact]
    public void Current_older_page_drops_when_a_mutation_replaced_its_boundary()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();
        generation.AdvanceForDeletion();

        Assert.Equal(HistoryReadCompletion.Drop, generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: false));
    }

    [Fact]
    public void A_read_kept_stale_by_every_retry_gives_up_after_the_budget()
    {
        var generation = new HistoryReadGeneration();
        for (var retries = 0; retries < HistoryReadGeneration.MaxAutomaticRetries; retries++)
        {
            var ticket = generation.Capture();
            generation.AdvanceForDeletion();
            Assert.Equal(HistoryReadCompletion.Retry, generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: true, retries));
        }

        var last = generation.Capture();
        generation.AdvanceForDeletion();

        Assert.Equal(
            HistoryReadCompletion.GiveUp,
            generation.CompleteRead(last, requestStillCurrent: true, retryWhenStale: true, HistoryReadGeneration.MaxAutomaticRetries));
    }

    [Fact]
    public void An_older_page_never_gives_up_or_retries_it_drops()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();
        generation.AdvanceForDeletion();

        Assert.Equal(
            HistoryReadCompletion.Drop,
            generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: false, HistoryReadGeneration.MaxAutomaticRetries));
    }

    [Fact]
    public void A_request_no_longer_current_drops_whatever_its_budget()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();
        generation.AdvanceForDeletion();

        Assert.Equal(HistoryReadCompletion.Drop, generation.CompleteRead(ticket, requestStillCurrent: false, retryWhenStale: true, 0));
    }

    [Fact]
    public void Each_retry_waits_longer_than_the_last()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), HistoryReadGeneration.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(300), HistoryReadGeneration.RetryDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(600), HistoryReadGeneration.RetryDelay(3));
        Assert.Equal(TimeSpan.FromMilliseconds(600), HistoryReadGeneration.RetryDelay(9));
    }

    [Fact]
    public void Current_unchanged_read_publishes()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        Assert.Equal(HistoryReadCompletion.Publish, generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: true));
    }
}
