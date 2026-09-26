using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// The pipe between the app and the overlay process carries text lines: a verb and, for some, one argument. The overlay
/// cannot reference Scribe.Core, so it parses the verbs from literals of its own, and the app's client must build every
/// line from <see cref="OverlayPipeProtocol"/>. These tests keep the three in step from source, in the spirit of the
/// POSITION anchors (the engine's <see cref="OverlayPosition"/> and the overlay's <c>OverlayAnchor</c>, kept equal by name).
/// </summary>
public sealed class OverlayPipeProtocolTests
{
    [Fact]
    public void Every_verb_the_app_can_send_is_one_the_overlay_parses_and_no_other()
    {
        var parsed = Regex.Matches(Dispatch(), "case \"(?<verb>[A-Z]+)\":").Select(m => m.Groups["verb"].Value).ToArray();

        Assert.Equal(parsed.Distinct().Count(), parsed.Length);
        Assert.Equal(OverlayPipeProtocol.Verbs.Order(StringComparer.Ordinal), parsed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_client_builds_every_line_from_the_protocol()
    {
        // A verb written as a literal in the client could drift from the overlay's parser without any test noticing.
        var literals = Regex.Matches(StripComments(File.ReadAllText(ClientFile())), "\"(?<text>(?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => m.Groups["text"].Value)
            .ToArray();
        Assert.NotEmpty(literals);

        var verbs = OverlayPipeProtocol.Verbs.ToHashSet();
        var shouted = literals
            .Select(text => Regex.Match(text, "^(?<word>[A-Z]{3,})(?: |$)"))
            .Where(m => m.Success)
            .Select(m => m.Groups["word"].Value)
            .ToArray();
        Assert.DoesNotContain(shouted, verbs.Contains);

        // What is left are the two log labels of commands that never reach the pipe.
        Assert.Equal(["KEEPWARM", "RELEASE"], shouted.Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(PillOutcomeKind.Typed, "TYPED")]
    [InlineData(PillOutcomeKind.TypedWithoutCleanup, "TYPEDWITHOUTCLEANUP")]
    [InlineData(PillOutcomeKind.NothingTyped, "NOTHINGTYPED")]
    [InlineData(PillOutcomeKind.PartlyTyped, "PARTLYTYPED")]
    public void Each_outcome_has_a_verb_named_after_it_that_the_overlay_shows_as_the_state_of_the_same_name(
        PillOutcomeKind kind, string verb)
    {
        Assert.Equal(verb, OverlayPipeProtocol.OutcomeVerb(kind));
        Assert.Equal(kind.ToString().ToUpperInvariant(), verb);
        Assert.Contains(verb, OverlayPipeProtocol.Verbs);

        Assert.Contains(kind.ToString(), EnumMembers(File.ReadAllText(OverlayFile("OverlayState.cs")), "OverlayState"));
        Assert.Matches(new Regex($"case \"{verb}\":\\s*_window\\.ShowOutcome\\(OverlayState\\.{kind}\\b"), Dispatch());
    }

    [Fact]
    public void An_outcome_line_carries_its_verb_and_its_second_line()
    {
        Assert.Equal("TYPED", OverlayPipeProtocol.OutcomeLine(Outcome(PillOutcomeKind.Typed)));
        Assert.Equal(
            "TYPEDWITHOUTCLEANUP AI cleanup timed out.",
            OverlayPipeProtocol.OutcomeLine(Outcome(PillOutcomeKind.TypedWithoutCleanup)));
        Assert.Equal(
            "NOTHINGTYPED Copy it from the tray menu",
            OverlayPipeProtocol.OutcomeLine(Outcome(PillOutcomeKind.NothingTyped)));
        Assert.Equal(
            "PARTLYTYPED Copy it from the tray menu",
            OverlayPipeProtocol.OutcomeLine(Outcome(PillOutcomeKind.PartlyTyped)));
        Assert.Equal(
            "NOTHINGTYPED Nothing was recognised, try again",
            OverlayPipeProtocol.OutcomeLine(PillOutcome.Of(null, false, null, "nothing was recognised, try again")!));
    }

    [Fact]
    public void An_argument_is_one_line_and_an_empty_one_is_left_off()
    {
        Assert.Equal("WARNING Microphone  muted", OverlayPipeProtocol.WarningLine(" Microphone\r\nmuted\n"));
        Assert.Equal("WARNING", OverlayPipeProtocol.WarningLine("   "));
        Assert.Equal("WARNING", OverlayPipeProtocol.WarningLine(null));
    }

    [Fact]
    public void The_other_lines_are_the_ones_the_overlay_reads()
    {
        Assert.Equal("PROCESSING 1", OverlayPipeProtocol.ProcessingLine(aiCleanup: true));
        Assert.Equal("PROCESSING 0", OverlayPipeProtocol.ProcessingLine(aiCleanup: false));
        Assert.Contains("_window.ShowProcessing(arg.Trim() == \"1\");", Dispatch(), StringComparison.Ordinal);

        Assert.Equal("METER 0", OverlayPipeProtocol.MeterLine(0));
        Assert.Equal("METER 1000", OverlayPipeProtocol.MeterLine(1000));
        Assert.Contains("_window.SetMeter(v / 1000.0);", Dispatch(), StringComparison.Ordinal);

        Assert.Equal("POSITION TopCenter", OverlayPipeProtocol.PositionLine(OverlayPosition.TopCenter));
        Assert.Equal("POSITION BottomRight", OverlayPipeProtocol.PositionLine(OverlayPosition.BottomRight));
    }

    [Fact]
    public void The_position_anchors_are_the_engine_s_positions_by_name()
    {
        var anchors = EnumMembers(File.ReadAllText(OverlayFile("OverlayAnchor.cs")), "OverlayAnchor");
        Assert.Equal(Enum.GetNames<OverlayPosition>(), anchors);
    }

    [Fact]
    public void A_state_that_hides_itself_is_never_replayed_and_is_kept_on_screen_for_its_hold()
    {
        // A relaunched or moved helper is told the latest state again. An outcome is shown once, by its own command:
        // replayed later, a settings save minutes after a dictation would flash its "Typed" again.
        var client = StripComments(File.ReadAllText(ClientFile()));
        Assert.Contains(
            "public string ReplayLine => Demand == OverlayDemand.Transient ? OverlayPipeProtocol.Hide : Line;",
            client,
            StringComparison.Ordinal);
        Assert.Contains("writer.WriteLine(_desired.ReplayLine);", client, StringComparison.Ordinal);
        Assert.Contains("Enqueue(desired.ReplayLine, desired, ensureAlive: false);", client, StringComparison.Ordinal);
        Assert.DoesNotContain("writer.WriteLine(_desired.Line);", client, StringComparison.Ordinal);
        Assert.DoesNotContain("Enqueue(_desired.Line", client, StringComparison.Ordinal);

        // The outcome is transient and needs the helper; it keeps the helper from when its write returns, never from when
        // it was taken (a write can take up to its timeout and still succeed).
        Assert.Matches(new Regex(@"new DesiredState\(OverlayPipeProtocol\.OutcomeLine\(outcome\), OverlayDemand\.Transient\)"), client);
        Assert.Matches(new Regex(@"Enqueue\(desired\.Line, desired, ensureAlive: true, showsFor: outcome\.OnScreen\)"), client);
        Assert.Contains(
            "_lifetime.OnStateCommand(\n            nowMs, item.Stamp, item.EnsureAlive, item.CancelsRetry, _desired.Demand, helper, IsSuperseded(item));",
            client.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"WriteWithTimeout\([^;]+\);\s*if \(item\.ShowsForMs > 0\)\s*\{\s*_lifetime\.OnShown\(Environment\.TickCount64, item\.ShowsForMs\);"),
            client);
        Assert.Single(Regex.Matches(client, Regex.Escape("_lifetime.OnShown(")));
    }

    [Fact]
    public void A_state_command_a_newer_state_replaced_is_never_written_and_an_anchor_move_writes_the_applied_anchor()
    {
        // Astra's A2 and every command of its shape: a queued state line written after a launch replayed something newer.
        var client = StripComments(File.ReadAllText(ClientFile())).ReplaceLineEndings("\n");

        // Every request is a state of its own, compared by reference; no state object is shared between two requests.
        Assert.Contains("private sealed class DesiredState(string line, OverlayDemand demand)", client, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"static DesiredState \w+ \{ get; \}"), client);
        Assert.Contains(
            "private bool IsSuperseded(Command item) => item.State is { } state && !ReferenceEquals(state, _desired);",
            client,
            StringComparison.Ordinal);
        foreach (var (method, published) in new[]
                 {
                     ("public void ShowRecording()", "var desired = DesiredState.Recording();"),
                     ("public void ShowProcessing(bool aiPolishing)", "var desired = DesiredState.Processing(aiPolishing);"),
                     ("public void ShowOutcome(PillOutcome outcome)", "var desired = new DesiredState(OverlayPipeProtocol.OutcomeLine(outcome), OverlayDemand.Transient);"),
                     ("public void HideOverlay()", "var desired = DesiredState.Hidden();"),
                     // A warning goes out for the live recording's state, and goes with it once a newer state replaces it.
                     ("public void ShowRecordingWarning(string? reason)", "var desired = _desired is { IsRecording: true } recording ? recording : DesiredState.Recording();"),
                 })
        {
            var body = Body(client, method);
            Assert.Contains(published, body, StringComparison.Ordinal);

            // Published before it is queued: the consumer judges a command against the latest state, so a command queued
            // first could be taken, judged stale and skipped, and the state it was made for would never be shown.
            Assert.Matches(new Regex(@"_desired = desired;[\s\S]*Enqueue\([^;]*, desired, "), body);
        }

        // The queue carries each command's state; without it no command is ever judged stale.
        Assert.Matches(
            new Regex(@"private void Enqueue\(string text, DesiredState\? state,[^)]*\) =>\s*EnqueueStamped\(new Command\([^;]*State: state\)\);"),
            client);

        // Judged before the decision (a stale command launches nothing) and again right before the write, after any launch.
        var handle = Body(client, "private void HandleState(Command item)");
        Assert.Equal(2, Regex.Matches(handle, Regex.Escape("IsSuperseded(item)")).Count);
        Assert.Matches(
            new Regex(@"if \(IsSuperseded\(item\)\)\s*\{[\s\S]*?return;\s*\}\s*WriteWithTimeout\(item\.AppliedAnchor \? AppliedAnchorLine : item\.Text\);"),
            handle);
        Assert.True(
            handle.IndexOf("Prepare(action, helper, nowMs)", StringComparison.Ordinal)
                < handle.LastIndexOf("IsSuperseded(item)", StringComparison.Ordinal),
            "The second judgement comes after the launch.");

        // The engine's anchor move writes the anchor as it stands when written, so it never puts back an older one.
        var move = Body(client, "public void SetPosition(OverlayPosition position)");
        Assert.Contains(
            "EnqueueStamped(new Command(CommandKind.State, OverlayPipeProtocol.PositionLine(position), AppliedAnchor: true));",
            move,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_shows_a_dictation_s_outcome_in_place_of_the_hide_and_nothing_else_on_the_pill()
    {
        // The relay's render callback: the outcome travels on the Idle change, so it keeps that change's revision.
        var shell = StripComments(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "App.xaml.cs")))
            .ReplaceLineEndings("\n");
        var render = shell[shell.IndexOf("private void RenderDictationState(DictationStateChange change)", StringComparison.Ordinal)..];
        render = render[..render.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Matches(
            new Regex(@"default:\s*if \(change\.Outcome is \{ \} outcome\)\s*\{\s*_overlay\?\.ShowOutcome\(outcome\);\s*\}\s*else\s*\{\s*_overlay\?\.HideOverlay\(\);\s*\}"),
            render);

        // The failure flash, which fired before the text was typed and for errors alike, is gone; the pill hears about a
        // failure only as an outcome.
        Assert.DoesNotContain("ShowFailed", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupFailed", shell, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(shell, Regex.Escape("_overlay?.ShowOutcome(")));
    }

    private static PillOutcome Outcome(PillOutcomeKind kind) => kind switch
    {
        PillOutcomeKind.Typed => PillOutcome.Of(new(true, "unicode", 2, 2), false, null, null)!,
        PillOutcomeKind.TypedWithoutCleanup => PillOutcome.Of(
            new(true, "unicode", 2, 2),
            true,
            new Cleanup.CleanupResult("x", Cleanup.CleanupOutcome.Failed, "AI cleanup timed out."),
            null)!,
        PillOutcomeKind.NothingTyped => PillOutcome.Of(new(false, "none", 0, 2), false, null, null)!,
        _ => PillOutcome.Of(new(false, "unicode", 1, 2), false, null, null)!,
    };

    // The text of a member from its signature to its matching closing brace.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
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

    // The member names of an enum as declared, comments and attributes aside.
    private static string[] EnumMembers(string source, string name)
    {
        var body = Regex.Match(StripComments(source), $@"enum {name}\s*\{{(?<body>[^}}]*)\}}", RegexOptions.Singleline);
        Assert.True(body.Success, $"enum {name} was not found.");
        return
        [
            .. body.Groups["body"].Value.Split(',')
                .Select(member => Regex.Replace(member, @"\[[^\]]*\]", string.Empty).Trim())
                .Where(member => member.Length > 0),
        ];
    }

    private static string Dispatch() => StripComments(File.ReadAllText(OverlayFile(Path.Combine("Ipc", "OverlayIpcServer.cs"))));

    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string OverlayFile(string name) => Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay", name);

    private static string ClientFile() => Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Overlay", "OverlayProcessClient.cs");

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
