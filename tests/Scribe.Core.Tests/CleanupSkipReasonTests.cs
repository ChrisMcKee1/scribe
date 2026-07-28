using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// A skipped cleanup must say why. Regression cover for a real incident: a deployment name that did
/// not exist on the configured endpoint failed validation once at startup, and every dictation for
/// the rest of the session then went out raw while logging nothing but an unexplained "Skipped".
/// The user believed cleanup was running for hours. "Switched off" and "switched on but broken"
/// must never again be indistinguishable.
/// </summary>
public class CleanupSkipReasonTests
{
    [Fact]
    public void A_skip_with_no_reason_is_the_user_turning_cleanup_off()
    {
        var result = CleanupResult.Skip("raw text");

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Null(result.SkipReason);
        Assert.False(result.SkippedUnexpectedly);
    }

    [Fact]
    public void A_skip_with_a_reason_is_flagged_as_unexpected()
    {
        var result = CleanupResult.Skip(
            "raw text", "AI cleanup is enabled but Unavailable (deployment not found).");

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.True(result.SkippedUnexpectedly);
        Assert.Contains("deployment not found", result.SkipReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_skip_never_loses_the_dictation()
    {
        // Whatever the reason, the raw text must pass through so speech is never dropped.
        const string raw = "send the report on wednesday";

        Assert.Equal(raw, CleanupResult.Skip(raw).Text);
        Assert.Equal(raw, CleanupResult.Skip(raw, "engine not ready").Text);
    }

    [Theory]
    [InlineData(CleanupOutcome.Cleaned)]
    [InlineData(CleanupOutcome.Unchanged)]
    [InlineData(CleanupOutcome.Failed)]
    public void Only_a_skipped_outcome_can_be_an_unexpected_skip(CleanupOutcome outcome)
    {
        // A runtime failure has its own visible path (it flashes the overlay red); it must not also
        // be reported as a silent skip or the two signals would double up.
        var result = new CleanupResult("text", outcome, FailureReason: "boom", SkipReason: "ignored");

        Assert.False(result.SkippedUnexpectedly);
    }

    [Fact]
    public void A_failed_outcome_keeps_reporting_its_failure_reason()
    {
        var result = new CleanupResult("raw", CleanupOutcome.Failed, FailureReason: "timed out");

        Assert.Equal("timed out", result.FailureReason);
        Assert.False(result.Changed);
    }
}
