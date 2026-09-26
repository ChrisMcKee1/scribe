namespace Scribe.Core.Settings;

public static class UsageMetricLayout
{
    public const double MinimumTileWidth = 128;

    public static int Columns(double availableWidth, double textScale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableWidth);

        var scale = TextScale.NormalizeFactor(textScale);
        if (availableWidth >= 6 * MinimumTileWidth * scale)
        {
            return 6;
        }

        if (availableWidth >= 3 * MinimumTileWidth * scale)
        {
            return 3;
        }

        return 2;
    }
}
