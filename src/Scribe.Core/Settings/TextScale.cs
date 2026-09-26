namespace Scribe.Core.Settings;

/// <summary>Applies Windows' text size factor to Scribe's type ramp.</summary>
public static class TextScale
{
    public const double MinimumFactor = 1;
    public const double MaximumFactor = 2.25;
    public const double MinimumSize = 12;

    public static double NormalizeFactor(double factor) =>
        double.IsFinite(factor) ? Math.Clamp(factor, MinimumFactor, MaximumFactor) : MinimumFactor;

    public static double Apply(double baseSize, double factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseSize);
        return Math.Max(MinimumSize, Math.Round(baseSize * NormalizeFactor(factor), MidpointRounding.AwayFromZero));
    }
}
