using Scribe.Core.Feedback;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the report that Store policy 11.16 requires. The privacy behaviour here is not a nicety:
/// the report carries the user's own words, and the whole design rests on the user choosing what
/// leaves the device.
/// </summary>
public class AiContentReportTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 28, 14, 5, 0, TimeSpan.Zero);

    [Fact]
    public void The_source_text_is_excluded_unless_the_reporter_opts_in()
    {
        // The single most sensitive thing in the report. A reviewer can usually judge the output on
        // its own, so including what the user actually said has to be their decision rather than a
        // default they never saw.
        var report = AiContentReport.Build(
            output: "REWRITTEN OUTPUT",
            provider: "AzureFoundry",
            model: "gpt-5.6-terra",
            appVersion: "0.3.14",
            whenUtc: When);

        Assert.Contains("REWRITTEN OUTPUT", report, StringComparison.Ordinal);
        Assert.DoesNotContain("SOURCE TEXT", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_text_is_included_and_labelled_when_the_reporter_asks()
    {
        var report = AiContentReport.Build(
            output: "REWRITTEN OUTPUT",
            provider: "FoundryLocal",
            model: "qwen3-1.7b",
            appVersion: "0.3.14",
            whenUtc: When,
            sourceText: "WHAT THE USER SAID");

        Assert.Contains("WHAT THE USER SAID", report, StringComparison.Ordinal);
        Assert.Contains("included by the reporter", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_names_the_model_that_produced_the_result()
    {
        // Prompts change between releases and providers behave differently, so a report that does
        // not say what produced the output cannot be acted on, and the policy requires acting.
        var report = AiContentReport.Build(
            "out", "AzureFoundry", "gpt-5.6-terra", "0.3.14", When);

        Assert.Contains("AzureFoundry", report, StringComparison.Ordinal);
        Assert.Contains("gpt-5.6-terra", report, StringComparison.Ordinal);
        Assert.Contains("0.3.14", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mailto_escapes_the_body_so_a_client_cannot_truncate_it()
    {
        // An unescaped newline or ampersand ends the URI early, and the failure is silent: the user
        // sees a composed message and never learns the part naming the problem was dropped.
        var body = AiContentReport.Build(
            "line one\nline two & three", "AzureFoundry", "m", "0.3.14", When);
        var uri = AiContentReport.BuildMailtoUri("subject with spaces", body);

        Assert.StartsWith($"mailto:{AiContentReport.SupportAddress}?subject=", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("line two & three", uri, StringComparison.Ordinal);
        Assert.Contains("%26", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void The_support_address_is_not_github()
    {
        // Certification rejected GitHub as a support contact in as many words, so this is a
        // regression pin rather than a preference.
        Assert.DoesNotContain("github", AiContentReport.SupportAddress, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@", AiContentReport.SupportAddress, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiRating.NotUseful)]
    [InlineData(AiRating.Useful)]
    [InlineData(AiRating.Unrated)]
    public void Only_ai_produced_results_can_be_reported(AiRating rating)
    {
        // Applying the user's own dictionary is a deterministic transformation, not generative AI
        // content. Reporting it to a developer as inappropriate AI output would be meaningless.
        Assert.True(AiContentReport.CanReport(rating, producedByAi: true));
        Assert.False(AiContentReport.CanReport(rating, producedByAi: false));
    }
}
