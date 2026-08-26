using System.Text.Json.Serialization;

namespace Scribe.StyleEval.Checks;

/// <summary>Verdict of one deterministic checker on one cell.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
internal enum CheckStatus
{
    /// <summary>The rule held.</summary>
    Pass,

    /// <summary>The rule was broken, or the structure the content warranted was missing.</summary>
    Fail,

    /// <summary>
    /// The checker had nothing to say: the scenario did not carry the expectation, or the
    /// destination cannot express the construct. Never a silent pass.
    /// </summary>
    NotApplicable,
}

/// <summary>One checker's verdict, with the reason it reached it.</summary>
/// <param name="Check">Checker name, stable across runs.</param>
/// <param name="Status">Pass, Fail or NotApplicable.</param>
/// <param name="Reason">Why. Always populated, including on a pass.</param>
/// <param name="Polarity">
/// Whether this is a rule violation (negative half) or a missed opportunity (positive half). The two
/// halves are reported separately because a suite that only counts the first is blind to a model
/// that formats nothing.
/// </param>
internal sealed record CheckResult(
    string Check,
    CheckStatus Status,
    string Reason,
    CheckPolarity Polarity)
{
    public static CheckResult Pass(string check, CheckPolarity polarity, string reason) =>
        new(check, CheckStatus.Pass, reason, polarity);

    public static CheckResult Fail(string check, CheckPolarity polarity, string reason) =>
        new(check, CheckStatus.Fail, reason, polarity);

    public static CheckResult Skip(string check, CheckPolarity polarity, string reason) =>
        new(check, CheckStatus.NotApplicable, reason, polarity);
}

/// <summary>Which half of the suite a checker belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckPolarity>))]
internal enum CheckPolarity
{
    /// <summary>The output broke a stated rule: over-formatted, dropped, invented, exceeded a band.</summary>
    Negative,

    /// <summary>The output missed structure the Detection rules say the content warranted.</summary>
    Positive,
}
