namespace Scribe.Core.Appearance;

/// <summary>One colour the recording pill draws, with what it is for.</summary>
/// <param name="Role">What the colour paints, in words.</param>
/// <param name="Color">The colour.</param>
public readonly record struct PillColour(string Role, SrgbColor Color);

/// <summary>
/// Every colour the recording pill draws in its normal look ("Signal On", section 5 of the palette decision). The pill is
/// the same in both app themes and never follows the Windows accent, so a red accent can never make listening look like an
/// error. The brand values are <see cref="ScribeBrand"/>'s own; the pill adds only its sheen, its two text tones and the
/// check and caution colours of Fluent's status icons. The overlay process cannot reference this assembly, so a test
/// checks every colour its XAML draws against <see cref="All"/>. In a contrast theme the pill draws system colours
/// instead, and none of these.
/// </summary>
public static class PillPalette
{
    /// <summary>The face's gradient, top.</summary>
    public static SrgbColor FaceTop => ScribeBrand.PillFaceTop;

    /// <summary>The face's gradient, bottom.</summary>
    public static SrgbColor FaceBottom => ScribeBrand.PillFaceBottom;

    /// <summary>
    /// The sheen at the very top of the face, fading to <see cref="SheenEnd"/> at <see cref="SheenFraction"/> of its
    /// height. Over the face's top it draws #2C3853, the lightest point of the face and the surface every ratio of the
    /// pill is measured on.
    /// </summary>
    public static readonly SrgbColor Sheen = SrgbColor.Parse("#14FFFFFF");

    /// <summary>Where the sheen has faded out: white at alpha 0, so the gradient fades rather than darkens.</summary>
    public static readonly SrgbColor SheenEnd = SrgbColor.Parse("#00FFFFFF");

    /// <summary>How far down the face the sheen reaches, as a fraction of its height.</summary>
    public const double SheenFraction = 0.45;

    /// <summary>The 1.5 DIP edge while the microphone is open.</summary>
    public static SrgbColor ListeningEdge => ScribeBrand.ListeningEdge;

    /// <summary>The 1 DIP edge while processing, and after all of the text was typed (with or without AI cleanup).</summary>
    public static SrgbColor NeutralEdge => ScribeBrand.NeutralEdge;

    /// <summary>The 1 DIP edge when nothing, or not all, of the text was typed.</summary>
    public static SrgbColor ErrorEdge => ScribeBrand.ErrorEdge;

    /// <summary>The words.</summary>
    public static readonly SrgbColor Text = SrgbColor.Parse("#FFFFFF");

    /// <summary>The second line of a notice (the reason, or the next step), at 12 like the first.</summary>
    public static readonly SrgbColor SecondaryText = SrgbColor.Parse("#C5FFFFFF");

    /// <summary>The level bars' tip (their top).</summary>
    public static SrgbColor LevelTip => ScribeBrand.LevelTip;

    /// <summary>The level bars' base (their bottom).</summary>
    public static SrgbColor LevelBase => ScribeBrand.LevelBase;

    /// <summary>The three processing dots: the level bars' tip, the same for transcribing and for AI cleanup.</summary>
    public static SrgbColor ProcessingDots => ScribeBrand.LevelTip;

    /// <summary>The check beside "Typed" (Fluent's success colour on a dark surface).</summary>
    public static readonly SrgbColor SuccessIcon = SrgbColor.Parse("#6CCB5F");

    /// <summary>The triangle beside "Typed without AI cleanup" (Fluent's caution colour on a dark surface).</summary>
    public static readonly SrgbColor CautionIcon = SrgbColor.Parse("#FCE100");

    /// <summary>The icon beside "Nothing typed" and "Not all of it was typed": the error edge's colour.</summary>
    public static SrgbColor ErrorIcon => ScribeBrand.ErrorEdge;

    /// <summary>Every colour above, once, with its role.</summary>
    public static IReadOnlyList<PillColour> All { get; } =
    [
        new("face top", FaceTop),
        new("face bottom", FaceBottom),
        new("sheen", Sheen),
        new("sheen end", SheenEnd),
        new("listening edge", ListeningEdge),
        new("neutral edge", NeutralEdge),
        new("error edge", ErrorEdge),
        new("text", Text),
        new("secondary text", SecondaryText),
        new("level tip", LevelTip),
        new("level base", LevelBase),
        new("processing dots", ProcessingDots),
        new("success icon", SuccessIcon),
        new("caution icon", CautionIcon),
        new("error icon", ErrorIcon),
    ];
}
