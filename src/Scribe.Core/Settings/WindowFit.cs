namespace Scribe.Core.Settings;

public readonly record struct WorkArea(double Left, double Top, double Width, double Height);

public readonly record struct WindowFitResult(
    double Left,
    double Top,
    double Width,
    double Height,
    double MinWidth,
    double MinHeight);

public static class WindowFit
{
    public const double DesiredWidth = 1240;
    public const double DesiredHeight = 900;
    public const double MinimumWidth = 940;
    public const double MinimumHeight = 660;
    public const double WidthFraction = 0.90;
    public const double HeightFraction = 0.92;

    public static WindowFitResult Compute(WorkArea workArea) =>
        Compute(DesiredWidth, DesiredHeight, MinimumWidth, MinimumHeight, workArea);

    public static WindowFitResult Compute(
        double desiredWidth,
        double desiredHeight,
        double minimumWidth,
        double minimumHeight,
        WorkArea workArea,
        double? requestedLeft = null,
        double? requestedTop = null)
    {
        if (!double.IsFinite(workArea.Width) || workArea.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), workArea.Width, "Work area width must be finite and positive.");
        }

        if (!double.IsFinite(workArea.Height) || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), workArea.Height, "Work area height must be finite and positive.");
        }

        var effectiveMinWidth = Math.Min(minimumWidth, workArea.Width);
        var effectiveMinHeight = Math.Min(minimumHeight, workArea.Height);
        var width = FitDimension(desiredWidth, effectiveMinWidth, workArea.Width, WidthFraction);
        var height = FitDimension(desiredHeight, effectiveMinHeight, workArea.Height, HeightFraction);
        var left = requestedLeft ?? workArea.Left + (workArea.Width - width) / 2;
        var top = requestedTop ?? workArea.Top + (workArea.Height - height) / 2;

        return new WindowFitResult(
            Clamp(left, workArea.Left, workArea.Left + workArea.Width - width),
            Clamp(top, workArea.Top, workArea.Top + workArea.Height - height),
            width,
            height,
            effectiveMinWidth,
            effectiveMinHeight);
    }

    private static double FitDimension(double desired, double effectiveMinimum, double available, double fraction) =>
        Math.Min(available, Math.Max(effectiveMinimum, Math.Min(desired, fraction * available)));

    private static double Clamp(double value, double min, double max) =>
        max <= min ? min : Math.Min(Math.Max(value, min), max);
}
