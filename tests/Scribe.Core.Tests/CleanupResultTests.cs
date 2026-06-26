using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the <see cref="CleanupResult"/> contract the dictation pipeline relies on to tell a
/// genuine runtime failure (raw fallback + visible warning) apart from a skip or an already-clean
/// transcript.
/// </summary>
public sealed class CleanupResultTests
{
    [Fact]
    public void Skip_passes_text_through_untouched_and_is_not_a_change()
    {
        var result = CleanupResult.Skip("hello world");

        Assert.Equal("hello world", result.Text);
        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Null(result.FailureReason);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Changed_is_true_only_for_a_cleaned_outcome()
    {
        Assert.True(new CleanupResult("x", CleanupOutcome.Cleaned).Changed);
        Assert.False(new CleanupResult("x", CleanupOutcome.Unchanged).Changed);
        Assert.False(new CleanupResult("x", CleanupOutcome.Failed, "boom").Changed);
        Assert.False(new CleanupResult("x", CleanupOutcome.Skipped).Changed);
    }

    [Fact]
    public void Failed_carries_the_raw_text_and_a_reason()
    {
        var result = new CleanupResult("raw transcription", CleanupOutcome.Failed, "AI cleanup timed out.");

        Assert.Equal("raw transcription", result.Text);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal("AI cleanup timed out.", result.FailureReason);
    }

    [Fact]
    public void Cleaned_may_carry_a_partial_failure_reason_without_being_a_hard_failure()
    {
        // A long chunked dictation where some segments failed: still the best cleaned output, no red flash.
        var result = new CleanupResult("mostly cleaned", CleanupOutcome.Cleaned, "Partial cleanup: 1 of 4 segments failed.");

        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.True(result.Changed);
        Assert.NotNull(result.FailureReason);
    }
}
