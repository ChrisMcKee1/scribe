using System.Globalization;
using Scribe.Core.Models;

namespace Scribe.Core.Overlay;

/// <summary>
/// The commands the app sends the overlay process, one line each over its pipe: a verb and, for some, one argument.
/// The app's client builds every line here. The overlay deliberately has no reference to this assembly, so it parses
/// the same verbs from literals of its own, and a test keeps its parser and <see cref="Verbs"/> equal, the way the
/// POSITION anchors are kept equal by name.
/// </summary>
public static class OverlayPipeProtocol
{
    /// <summary>Launches the helper ahead of the first show; the window is built hidden, so it does nothing else.</summary>
    public const string Warmup = "WARMUP";

    /// <summary>Listening: the listening edge and the level bars.</summary>
    public const string Recording = "RECORDING";

    /// <summary>A warning shown over a live recording, with its text; the recording state stays.</summary>
    public const string Warning = "WARNING";

    /// <summary>Processing, with <c>1</c> when AI cleanup runs and <c>0</c> when only transcription does.</summary>
    public const string Processing = "PROCESSING";

    /// <summary>The outcome <see cref="PillOutcomeKind.Typed"/>.</summary>
    public const string Typed = "TYPED";

    /// <summary>The outcome <see cref="PillOutcomeKind.TypedWithoutCleanup"/>, with its reason.</summary>
    public const string TypedWithoutCleanup = "TYPEDWITHOUTCLEANUP";

    /// <summary>The outcome <see cref="PillOutcomeKind.NothingTyped"/>, with its next step.</summary>
    public const string NothingTyped = "NOTHINGTYPED";

    /// <summary>The outcome <see cref="PillOutcomeKind.PartlyTyped"/>, with its next step.</summary>
    public const string PartlyTyped = "PARTLYTYPED";

    /// <summary>
    /// The AI cleanup failure flash the outcomes replace, with its reason. Only the shell's cleanup failure and error
    /// handlers still send it; it goes once the shell hands on outcomes instead.
    /// </summary>
    public const string Failed = "FAILED";

    /// <summary>Hides the pill, unless an outcome is still holding on screen.</summary>
    public const string Hide = "HIDE";

    /// <summary>The live input level, from 0 to 1000.</summary>
    public const string Meter = "METER";

    /// <summary>The anchor, by the name of an <see cref="OverlayPosition"/> value.</summary>
    public const string Position = "POSITION";

    /// <summary>Asks the helper to close itself.</summary>
    public const string Exit = "EXIT";

    /// <summary>Every verb the app can send, and so every verb the overlay must parse.</summary>
    public static IReadOnlyList<string> Verbs { get; } =
    [
        Warmup, Recording, Warning, Processing, Typed, TypedWithoutCleanup, NothingTyped, PartlyTyped, Failed, Hide, Meter,
        Position, Exit,
    ];

    /// <summary>The verb that shows <paramref name="kind"/>: its name, in capitals.</summary>
    public static string OutcomeVerb(PillOutcomeKind kind) => kind switch
    {
        PillOutcomeKind.Typed => Typed,
        PillOutcomeKind.TypedWithoutCleanup => TypedWithoutCleanup,
        PillOutcomeKind.NothingTyped => NothingTyped,
        PillOutcomeKind.PartlyTyped => PartlyTyped,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No verb shows this outcome."),
    };

    /// <summary>The line that shows <paramref name="outcome"/>: its verb and, for a notice, its second line.</summary>
    public static string OutcomeLine(PillOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Line(OutcomeVerb(outcome.Kind), outcome.Detail);
    }

    /// <summary>A warning over the live recording.</summary>
    public static string WarningLine(string? reason) => Line(Warning, reason);

    /// <summary>Processing, saying whether AI cleanup runs for this capture.</summary>
    public static string ProcessingLine(bool aiCleanup) => Processing + (aiCleanup ? " 1" : " 0");

    /// <summary>The AI cleanup failure flash the outcomes replace.</summary>
    public static string FailedLine(string? reason) => Line(Failed, reason);

    /// <summary>The live input level, scaled to 0 to 1000.</summary>
    public static string MeterLine(int level) => Meter + " " + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>The anchor.</summary>
    public static string PositionLine(OverlayPosition position) => Position + " " + position;

    // One line per command: a line break inside an argument would end the line early and start a stray command.
    private static string Line(string verb, string? argument)
    {
        var clean = (argument ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length == 0 ? verb : verb + " " + clean;
    }
}
