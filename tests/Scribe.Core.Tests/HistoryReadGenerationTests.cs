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
    public void Current_unchanged_read_publishes()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        Assert.Equal(HistoryReadCompletion.Publish, generation.CompleteRead(ticket, requestStillCurrent: true, retryWhenStale: true));
    }
}
