namespace Scribe.Core.Settings;

public readonly record struct SettingRowLayoutInput(
    double AvailableWidth,
    double TextScale,
    double ControlMinimumWidth,
    double TextMinimumWidth = 320,
    double Gap = 16);

public static class SettingRowLayout
{
    public static bool StackControl(SettingRowLayoutInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input.AvailableWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(input.ControlMinimumWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(input.TextMinimumWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Gap);

        var scale = TextScale.NormalizeFactor(input.TextScale);
        return input.AvailableWidth < input.ControlMinimumWidth + input.Gap + input.TextMinimumWidth * scale;
    }
}
