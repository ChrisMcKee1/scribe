namespace Scribe.Core.Settings;

public readonly record struct ProfileLayoutInput(
    double AvailableHeight,
    double TextScale,
    double NoticeHeight,
    double HintHeight,
    double ToolbarHeight,
    double RowHeight = 52);

public static class ProfileLayoutPlanner
{
    public const int MinimumUsableRows = 4;

    public static bool UseCompact(ProfileLayoutInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input.AvailableHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(input.NoticeHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(input.HintHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(input.ToolbarHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.RowHeight);

        var scale = TextScale.NormalizeFactor(input.TextScale);
        var listHeight = input.AvailableHeight - input.NoticeHeight - input.HintHeight - input.ToolbarHeight;
        return listHeight < input.RowHeight * scale * MinimumUsableRows;
    }
}
