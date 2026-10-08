using System.Globalization;

namespace Scribe.Core.Overlay;

public static class OverlayExitCode
{
    public static string Format(int? code) =>
        code is { } value ? "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture) : "unset";
}
