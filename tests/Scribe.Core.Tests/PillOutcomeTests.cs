using Scribe.Core.Cleanup;
using Scribe.Core.Overlay;
using Scribe.Core.PostProcessing;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// What the recording pill says about a finished dictation (<see cref="PillOutcome.Of"/>), under the truth rules of the
/// palette decision: "Typed" and "Typed without AI cleanup" appear only after the whole insertion succeeded, the space
/// after the dictation included; a partial insertion, or a dictation left for the recovery copy, is the error state and
/// never a check.
/// </summary>
public sealed class PillOutcomeTests
{
    private static readonly InjectionResult Whole = new(true, "unicode", 6, 6);

    [Fact]
    public void A_dictation_typed_whole_without_AI_cleanup_asked_for_is_Typed()
    {
        var outcome = PillOutcome.Of(Whole, cleanupRequested: false, CleanupResult.Skip("Hello."), failure: null);

        Assert.NotNull(outcome);
        Assert.Equal(PillOutcomeKind.Typed, outcome.Kind);
        Assert.Equal(string.Empty, outcome.Detail);
    }

    [Theory]
    [InlineData(CleanupOutcome.Cleaned)]
    [InlineData(CleanupOutcome.Unchanged)]
    public void A_dictation_AI_cleanup_ran_on_is_Typed(CleanupOutcome ran)
    {
        var outcome = PillOutcome.Of(Whole, cleanupRequested: true, new CleanupResult("Hello.", ran), failure: null);

        Assert.Equal(PillOutcomeKind.Typed, outcome!.Kind);
    }

    [Fact]
    public void A_partly_degraded_cleanup_still_cleaned_the_text_so_it_is_Typed()
    {
        // Some segments of a long dictation failed and the rest were cleaned: no hard failure, as the failure log says.
        var partial = new CleanupResult("Cleaned. raw tail", CleanupOutcome.Cleaned, FailureReason: "AI cleanup timed out.");

        Assert.Equal(PillOutcomeKind.Typed, PillOutcome.Of(Whole, true, partial, null)!.Kind);
    }

    [Fact]
    public void A_deliberate_skip_is_not_a_missing_cleanup()
    {
        // Nothing handed over (a library scope withdrawn mid-dictation), or nothing to clean: skipped with no reason.
        Assert.Equal(PillOutcomeKind.Typed, PillOutcome.Of(Whole, true, CleanupResult.Skip("Hi."), null)!.Kind);
    }

    [Fact]
    public void A_failed_cleanup_with_the_text_typed_whole_is_Typed_without_AI_cleanup_and_its_safe_reason()
    {
        var failed = new CleanupResult("raw text", CleanupOutcome.Failed, FailureReason: "AI cleanup timed out.")
        {
            DisplayDetail = "The endpoint at contoso.example did not answer.",
        };

        var outcome = PillOutcome.Of(Whole, cleanupRequested: true, failed, failure: null);

        Assert.Equal(PillOutcomeKind.TypedWithoutCleanup, outcome!.Kind);
        Assert.Equal("AI cleanup timed out.", outcome.Detail);
        Assert.DoesNotContain("contoso", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleanup_that_was_on_but_not_ready_is_Typed_without_AI_cleanup_and_the_skip_reason()
    {
        var notReady = CleanupResult.Skip("raw text", "AI cleanup is enabled but Initializing (Loading the model.).") with
        {
            DisplayDetail = "Connecting to contoso.example.",
        };

        var outcome = PillOutcome.Of(Whole, cleanupRequested: true, notReady, failure: null);

        Assert.Equal(PillOutcomeKind.TypedWithoutCleanup, outcome!.Kind);
        Assert.Equal("AI cleanup is enabled but Initializing (Loading the model.).", outcome.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_missing_cleanup_with_no_reason_still_says_why(string? reason)
    {
        var failed = new CleanupResult("raw text", CleanupOutcome.Failed, FailureReason: reason);

        var outcome = PillOutcome.Of(Whole, true, failed, null);

        Assert.Equal(PillOutcomeKind.TypedWithoutCleanup, outcome!.Kind);
        Assert.Equal(PillOutcome.CleanupDidNotRun, outcome.Detail);
    }

    [Fact]
    public void Cleanup_that_was_not_asked_for_is_never_reported_missing()
    {
        // The dictation-only hotkey turns AI cleanup off for its capture, whatever the result object says.
        var failed = new CleanupResult("raw text", CleanupOutcome.Failed, FailureReason: "AI cleanup failed.");

        Assert.Equal(PillOutcomeKind.Typed, PillOutcome.Of(Whole, cleanupRequested: false, failed, null)!.Kind);
    }

    [Fact]
    public void Nothing_that_reached_the_target_is_Nothing_typed_with_the_recovery_step()
    {
        var focusMoved = new InjectionResult(false, "none", 0, 6, InjectionResult.FocusChangedError);

        var outcome = PillOutcome.Of(focusMoved, cleanupRequested: false, CleanupResult.Skip("x"), failure: null);

        Assert.Equal(PillOutcomeKind.NothingTyped, outcome!.Kind);
        Assert.Equal(PillOutcome.RecoveryStep, outcome.Detail);
        Assert.Equal("Copy it from the tray menu", PillOutcome.RecoveryStep);
    }

    [Fact]
    public void Part_of_the_text_is_Not_all_typed_and_never_a_check()
    {
        var partial = new InjectionResult(false, "unicode", 3, 6, "Only part of the text was accepted by Windows.");

        var outcome = PillOutcome.Of(partial, cleanupRequested: false, CleanupResult.Skip("x"), failure: null);

        Assert.Equal(PillOutcomeKind.PartlyTyped, outcome!.Kind);
        Assert.Equal(PillOutcome.RecoveryStep, outcome.Detail);
    }

    [Fact]
    public void A_stall_on_the_space_after_the_dictation_is_Not_all_typed()
    {
        // The space is part of what the target is given (DictationInsertion), so the whole insertion includes it.
        var insertion = DictationInsertion.Insert(
            "Hello.",
            addSpaceAfterDictation: true,
            new LastTranscriptStore(),
            typed => new InjectionResult(false, "unicode", typed.Length - 1, typed.Length, "Only part of the text was accepted by Windows."));

        Assert.True(insertion.SpaceAdded);
        var outcome = PillOutcome.Of(insertion.Injection, cleanupRequested: false, CleanupResult.Skip("x"), failure: null);

        Assert.Equal(PillOutcomeKind.PartlyTyped, outcome!.Kind);
    }

    [Fact]
    public void A_failed_insertion_is_the_error_state_even_when_cleanup_failed_too()
    {
        var failed = new CleanupResult("raw text", CleanupOutcome.Failed, FailureReason: "AI cleanup timed out.");
        var nothing = new InjectionResult(false, "none", 0, 9, InjectionResult.FocusChangedError);
        var partial = new InjectionResult(false, "unicode", 4, 9);

        Assert.Equal(PillOutcomeKind.NothingTyped, PillOutcome.Of(nothing, true, failed, null)!.Kind);
        Assert.Equal(PillOutcomeKind.PartlyTyped, PillOutcome.Of(partial, true, failed, null)!.Kind);
    }

    [Fact]
    public void A_delivered_paste_whose_modifier_cleanup_was_partial_is_Typed()
    {
        // The chord inserted the whole text; Sent and Total count its key events (TextInjector).
        var pasted = new InjectionResult(true, "clipboard", 3, 4, "Paste completed, but modifier cleanup was partial.");

        Assert.Equal(PillOutcomeKind.Typed, PillOutcome.Of(pasted, false, CleanupResult.Skip("x"), null)!.Kind);
    }

    [Fact]
    public void An_insertion_of_nothing_says_nothing()
    {
        Assert.Null(PillOutcome.Of(InjectionResult.Empty, cleanupRequested: false, CleanupResult.Skip(string.Empty), failure: null));
    }

    [Fact]
    public void A_dictation_that_failed_before_insertion_is_Nothing_typed_with_its_own_next_step()
    {
        var outcome = PillOutcome.Of(insertion: null, cleanupRequested: true, cleanup: null, "nothing was recognised, try again");

        Assert.Equal(PillOutcomeKind.NothingTyped, outcome!.Kind);
        Assert.Equal("Nothing was recognised, try again", outcome.Detail);
    }

    [Fact]
    public void A_failure_message_keeps_what_it_names()
    {
        var outcome = PillOutcome.Of(null, false, null, "no audio from 'USB Mic'. Pick a different microphone in Settings");

        Assert.Equal("No audio from 'USB Mic'. Pick a different microphone in Settings", outcome!.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_dictation_discarded_quietly_says_nothing(string? failure)
    {
        // No speech was heard, or the dictionary left nothing to type: the pill hides, as before.
        Assert.Null(PillOutcome.Of(insertion: null, cleanupRequested: true, cleanup: null, failure));
    }

    [Fact]
    public void The_insertion_decides_even_when_a_failure_was_reported_after_it()
    {
        // A handler that throws after the text arrived turns into "transcription failed"; the text is still there.
        Assert.Equal(PillOutcomeKind.Typed, PillOutcome.Of(Whole, false, CleanupResult.Skip("x"), "transcription failed")!.Kind);
    }

    [Fact]
    public void The_second_line_is_one_line()
    {
        var failed = new CleanupResult("raw", CleanupOutcome.Failed, FailureReason: "  AI cleanup failed.\r\nTry again.\n");

        var outcome = PillOutcome.Of(Whole, true, failed, null);

        Assert.Equal("AI cleanup failed.  Try again.", outcome!.Detail);
        Assert.Equal("Microphone unavailable", PillOutcome.Of(null, false, null, "microphone unavailable\r\n")!.Detail);
    }

    [Fact]
    public void Typed_is_brief_and_a_notice_holds_long_enough_to_read()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), PillTiming.TypedHold);
        Assert.Equal(TimeSpan.FromMilliseconds(1300), PillTiming.NoticeHold);
        Assert.Equal(TimeSpan.FromMilliseconds(1800), PillTiming.RecordingWarningHold);
        Assert.Equal(TimeSpan.FromMilliseconds(120), PillTiming.FadeIn);
        Assert.Equal(TimeSpan.FromMilliseconds(150), PillTiming.FadeOut);

        var typed = PillOutcome.Of(Whole, false, CleanupResult.Skip("x"), null)!;
        var caution = PillOutcome.Of(Whole, true, new CleanupResult("x", CleanupOutcome.Failed, "AI cleanup failed."), null)!;
        var nothing = PillOutcome.Of(null, false, null, "transcription failed")!;
        var partly = PillOutcome.Of(new InjectionResult(false, "unicode", 1, 2), false, null, null)!;

        Assert.Equal(PillTiming.TypedHold, typed.Hold);
        Assert.Equal(PillTiming.NoticeHold, caution.Hold);
        Assert.Equal(PillTiming.NoticeHold, nothing.Hold);
        Assert.Equal(PillTiming.NoticeHold, partly.Hold);

        // What the helper's lifetime keeps the helper for: until the pill has faded out.
        Assert.Equal(TimeSpan.FromMilliseconds(550), typed.OnScreen);
        Assert.Equal(TimeSpan.FromMilliseconds(1450), nothing.OnScreen);
    }
}
