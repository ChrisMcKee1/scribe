namespace Scribe.Core.TextInjection;

/// <summary>Pure fidelity rules for the opt-in strict injection lab, with no desktop access.</summary>
public static class InjectionLabValidation
{
    public static string Normalize(string text, bool strict)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return strict ? normalized : normalized.TrimEnd('\n');
    }

    public static bool Matches(string expected, string actual, bool strict) =>
        string.Equals(Normalize(expected, strict), Normalize(actual, strict), StringComparison.Ordinal);

    public static bool EnterCountsMatch(string expected, bool shiftEnter, int plainEnters, int shiftedEnters)
    {
        var breaks = 0;
        foreach (var ch in Normalize(expected, strict: true))
        {
            if (ch == '\n')
            {
                breaks++;
            }
        }
        return shiftEnter
            ? plainEnters == 0 && shiftedEnters == breaks
            : shiftedEnters == 0 && plainEnters == breaks;
    }

    public static double? CompletedMilliseconds(double producerMs, double? consumedMs) =>
        consumedMs is { } observed && double.IsFinite(observed) && observed >= 0 &&
        double.IsFinite(producerMs) && producerMs >= 0
            ? Math.Max(producerMs, observed)
            : null;
}
