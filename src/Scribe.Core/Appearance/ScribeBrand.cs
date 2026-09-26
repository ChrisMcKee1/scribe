namespace Scribe.Core.Appearance;

/// <summary>
/// The four colours of Scribe blue in one theme, in the order WPF-UI's four-colour
/// <c>ApplicationAccentColorManager.Apply(system, primary, secondary, tertiary)</c> takes them.
/// </summary>
/// <param name="System">The text selection background.</param>
/// <param name="Primary">Checks, the on switch track and selected items; the accent button in the light theme.</param>
/// <param name="Secondary">Accent text and links; the accent button in the dark theme.</param>
/// <param name="Tertiary">Secondary accent text and link hover.</param>
public sealed record AccentSet(SrgbColor System, SrgbColor Primary, SrgbColor Secondary, SrgbColor Tertiary);

/// <summary>
/// The brand colours ("Signal On"), all taken from the icon: its navy field (Ink), its white capsule (Paper) and its blue
/// waveform (Signal). Every surface that draws them reads them from here, or is checked against these values by a test,
/// because the overlay process and the icon files cannot reference this assembly.
/// </summary>
public static class ScribeBrand
{
    /// <summary>The icon's field, the idle and processing tray tiles, and the dark ink the recording waveform turns.</summary>
    public static readonly SrgbColor Ink = SrgbColor.Parse("#07142F");

    /// <summary>The icon's capsule.</summary>
    public static readonly SrgbColor Paper = SrgbColor.Parse("#FCFCFC");

    /// <summary>The icon's waveform, and the recording tray tile: the one state that lights the tile.</summary>
    public static readonly SrgbColor Signal = SrgbColor.Parse("#1C83FE");

    /// <summary>The paused tray tile.</summary>
    public static readonly SrgbColor Slate = SrgbColor.Parse("#6B7689");

    /// <summary>The processing tray glyph's three dots.</summary>
    public static readonly SrgbColor ProcessingDots = SrgbColor.Parse("#82B6FF");

    /// <summary>Scribe blue for the light theme.</summary>
    public static readonly AccentSet LightAccent = new(
        SrgbColor.Parse("#2461E9"), SrgbColor.Parse("#0C48CF"), SrgbColor.Parse("#0035B1"), SrgbColor.Parse("#00298E"));

    /// <summary>Scribe blue for the dark theme.</summary>
    public static readonly AccentSet DarkAccent = new(
        SrgbColor.Parse("#2461E9"), SrgbColor.Parse("#72A0FF"), SrgbColor.Parse("#88B0FE"), SrgbColor.Parse("#A1C1FE"));

    /// <summary>The recording indicator's face, top to bottom; the same in both app themes.</summary>
    public static readonly SrgbColor PillFaceTop = SrgbColor.Parse("#1A2744");

    /// <inheritdoc cref="PillFaceTop"/>
    public static readonly SrgbColor PillFaceBottom = SrgbColor.Parse("#0E1830");

    /// <summary>The recording indicator's edge while the microphone is open (1.5 DIP, no glow).</summary>
    public static readonly SrgbColor ListeningEdge = SrgbColor.Parse("#71ADFF");

    /// <summary>The recording indicator's edge while processing, and after text was typed (1 DIP).</summary>
    public static readonly SrgbColor NeutralEdge = SrgbColor.Parse("#8A9BB5");

    /// <summary>The recording indicator's edge when nothing, or not all, was typed (1 DIP).</summary>
    public static readonly SrgbColor ErrorEdge = SrgbColor.Parse("#FF99A4");

    /// <summary>The level bars' tip and base, and the pill's processing dots (the tip colour).</summary>
    public static readonly SrgbColor LevelTip = SrgbColor.Parse("#93C0FE");

    /// <inheritdoc cref="LevelTip"/>
    public static readonly SrgbColor LevelBase = SrgbColor.Parse("#549DFF");
}
