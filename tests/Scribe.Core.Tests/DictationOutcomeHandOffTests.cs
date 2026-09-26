using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The controller hands the pill a finished dictation's outcome and decides nothing about it: <c>PillOutcome.Of</c>
/// (tested in <see cref="PillOutcomeTests"/>) decides from what the pipeline set on its way, and the outcome is raised with
/// the dictation's return to idle, under that change's revision, after the lifecycle already accepts the next press. The
/// controller has no tests of its own (AGENTS.md, "Dictation lifecycle and shutdown"), so this pins it from source, as
/// <see cref="DictationInsertionTests"/> does for the insertion step.
/// </summary>
public sealed class DictationOutcomeHandOffTests
{
    private static readonly string Controller = ReadSource("src", "Scribe.App", "Dictation", "DictationController.cs");

    [Fact]
    public void Processing_decides_its_outcome_once_from_what_the_pipeline_set()
    {
        var process = Body(Controller, "private async Task ProcessAsync(");

        Assert.Single(Regex.Matches(Controller, Regex.Escape("PillOutcome.Of(pillInsertion,")));
        Assert.Contains(
            "ResetToIdle(session.Id, insertedTimestamp, PillOutcome.Of(pillInsertion, settings.EnableAiCleanup, pillCleanup, pillFailure));",
            process[process.LastIndexOf("finally", StringComparison.Ordinal)..],
            StringComparison.Ordinal);

        // The insertion as the insertion step reported it (the whole of it, the space included), and cleanup as it returned.
        Assert.Matches(new Regex(@"var injection = insertion\.Injection;\s*pillInsertion = injection;"), process);
        Assert.Matches(new Regex(@"report\.Cleanup = cleanup;\s*pillCleanup = cleanup;"), process);
    }

    [Fact]
    public void Every_failure_processing_reports_reaches_the_pill_as_well()
    {
        var process = Body(Controller, "private async Task ProcessAsync(");

        var raised = Regex.Matches(process, @"RaiseError\((?<message>[^;]*)\);").Select(m => m.Groups["message"].Value).ToArray();
        Assert.Equal(5, raised.Length);
        Assert.All(raised, message => Assert.Equal("pillFailure", message));

        // Five messages set where processing raises them, and a silent capture's two, which come back from the helper
        // that raised them.
        Assert.Equal(7, Regex.Matches(process, @"(?<!string\? )pillFailure = ").Count);
        Assert.Equal(2, SilentAssignments(process));
        Assert.Contains("private string? RaiseSilentCaptureError(", Controller, StringComparison.Ordinal);
    }

    [Fact]
    public void A_microphone_that_never_opened_says_nothing_was_typed()
    {
        Assert.Contains(
            "AbandonRecording(id, PillOutcome.Of(insertion: null, cleanupRequested: false, cleanup: null, failure));",
            Controller,
            StringComparison.Ordinal);
        var abandon = Body(Controller, "private void AbandonRecording(");
        Assert.Contains("Raise(idle.Presentation, outcome: outcome);", abandon, StringComparison.Ordinal);
    }

    [Fact]
    public void The_outcome_rides_the_idle_change_and_nothing_waits_for_it()
    {
        var reset = Body(Controller, "private void ResetToIdle(");
        var returned = reset.IndexOf("_lifecycle.ReturnToIdle(", StringComparison.Ordinal);
        var raised = reset.IndexOf("Raise(idle.Presentation, outcome: outcome);", StringComparison.Ordinal);
        Assert.InRange(returned, 0, raised - 1);

        // Only the two returns to idle that end a dictation carry one, as a field of the change the relay orders.
        Assert.Equal(2, Regex.Matches(Controller, Regex.Escape("outcome: outcome")).Count);
        Assert.Contains("new DictationStateChange(state, shown.Revision, aiPolishing, outcome)", Controller, StringComparison.Ordinal);
        Assert.Contains(
            "internal readonly record struct DictationStateChange(DictationState State, long Revision, bool AiPolishing, PillOutcome? Outcome = null);",
            Controller,
            StringComparison.Ordinal);

        // Logged by kind only: the detail can name a microphone.
        Assert.Contains("(object?)outcome?.Kind ?? \"None\"", Controller, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome.Detail", Controller, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome?.Detail", Controller, StringComparison.Ordinal);
    }

    private static int SilentAssignments(string process) =>
        Regex.Matches(process, @"RaiseSilentCaptureError\(report, ""[^""]+""\) is \{ \} silent\)\s*\{\s*pillFailure = silent;").Count;

    // The text of a member from its signature to its matching closing brace.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found in the controller.");
        var open = code.IndexOf('{', code.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts])).ReplaceLineEndings("\n");
    }
}
