namespace Scribe.Core.Settings;

public readonly record struct ListPaneLayoutInput(double ContentWidth, double TextScale, double BaseWidth = 270);

public static class ListPaneLayoutPlanner
{
    public static double ListWidth(ListPaneLayoutInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input.ContentWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.BaseWidth);

        var scale = TextScale.NormalizeFactor(input.TextScale);
        var scaled = input.BaseWidth * scale;
        if (input.ContentWidth <= 0)
        {
            return scaled;
        }

        return Math.Min(scaled, input.ContentWidth / 2);
    }
}
