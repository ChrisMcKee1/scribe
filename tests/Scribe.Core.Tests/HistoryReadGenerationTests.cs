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
    public void Failed_rating_does_not_make_a_read_stale()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        generation.CompleteRatingWrite(saved: false);

        Assert.True(generation.IsCurrent(ticket));
    }

    [Fact]
    public void Older_page_started_before_a_mutation_is_stale()
    {
        var generation = new HistoryReadGeneration();
        var ticket = generation.Capture();

        generation.AdvanceForDeletion();

        Assert.False(generation.IsCurrent(ticket));
    }
}
