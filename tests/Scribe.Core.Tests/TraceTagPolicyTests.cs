using System.Globalization;
using System.Reflection;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class TraceTagPolicyTests
{
    private const string TranscriptLike = "please send the quarterly numbers to Dana before Friday";
    private const string HostLike = "gpu-box.contoso.internal";

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    [Fact]
    public void An_ordinary_span_renders_the_way_the_log_has_always_shown_it()
    {
        var tags = new[]
        {
            Tag(ScribeTelemetry.TagCaptureSeconds, 3.52),
            Tag(ScribeTelemetry.TagVadEnabled, true),
            Tag(ScribeTelemetry.TagDecodeChars, 42),
            Tag(ScribeTelemetry.TagAiOutcome, "Cleaned"),
            Tag(ScribeTelemetry.TagTargetApp, "notepad"),
            Tag(ScribeTelemetry.TagOutcome, DictationOutcome.Injected),
        };

        var span = TraceTagPolicy.FormatSpan(ScribeTelemetry.DictationActivity, tags, TimeSpan.FromMilliseconds(1234.7));

        Assert.Equal(
            "dictation.process capture_seconds=3.52 vad_enabled=True decode_chars=42 ai_outcome=Cleaned " +
            "target_app=notepad outcome=injected (1234ms)",
            span);
    }

    [Fact]
    public void Transcript_or_host_values_on_unknown_tags_never_reach_the_output()
    {
        var tags = new[]
        {
            Tag("scribe.transcript", TranscriptLike),
            Tag("http.host", HostLike),
            Tag("server.address", HostLike),
            Tag(ScribeTelemetry.TagFinalChars, 55),
        };

        var span = TraceTagPolicy.FormatSpan(ScribeTelemetry.DictationActivity, tags, TimeSpan.FromMilliseconds(5));

        Assert.DoesNotContain("quarterly", span);
        Assert.DoesNotContain("contoso", span);
        Assert.DoesNotContain("transcript", span);
        Assert.DoesNotContain("host", span);
        Assert.Contains("final_chars=55", span);
        Assert.Contains("omitted_tags=3", span);
    }

    [Fact]
    public void A_free_text_skip_reason_reads_as_present_but_its_text_is_not_shown()
    {
        // The historical value, including the host it leaked.
        var reason = $"AI cleanup is enabled but Initializing (Connecting to {HostLike}\u2026).";

        var span = TraceTagPolicy.FormatSpan(
            ScribeTelemetry.DictationActivity, [Tag(ScribeTelemetry.TagAiSkipReason, reason)], TimeSpan.Zero);

        Assert.Contains("ai_skip_reason=(omitted)", span);
        Assert.DoesNotContain("contoso", span);
        Assert.DoesNotContain("Connecting", span);
    }

    [Fact]
    public void A_fixed_reason_code_is_shown_as_is()
    {
        var span = TraceTagPolicy.FormatSpan(
            ScribeTelemetry.DictationActivity, [Tag(ScribeTelemetry.TagAiSkipReason, "not-ready")], TimeSpan.Zero);

        Assert.Contains("ai_skip_reason=not-ready", span);
    }

    [Theory]
    [InlineData("scribe.decode_chars", "42 chars of: " + TranscriptLike)]
    [InlineData("scribe.vad_kept", "yes")]
    [InlineData("scribe.outcome", TranscriptLike)]
    [InlineData("scribe.inject.method", HostLike)]
    [InlineData("scribe.target_app", "C:\\Users\\someone\\secret.exe")]
    public void A_known_tag_with_the_wrong_shape_of_value_is_omitted(string key, object value)
    {
        Assert.True(TraceTagPolicy.TryFormatValue(key, value, out var formatted));
        Assert.Equal(TraceTagPolicy.OmittedValue, formatted);
    }

    [Fact]
    public void Numbers_render_in_the_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.True(TraceTagPolicy.TryFormatValue(ScribeTelemetry.TagRealTimeFactor, 0.05, out var formatted));
            Assert.Equal("0.05", formatted);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Enum_values_on_code_tags_are_shown_by_name()
    {
        Assert.True(TraceTagPolicy.TryFormatValue(
            ScribeTelemetry.TagAiOutcome, Scribe.Core.Cleanup.CleanupOutcome.Skipped, out var formatted));
        Assert.Equal("Skipped", formatted);
    }

    [Fact]
    public void Every_tag_Scribe_defines_is_on_the_allowlist()
    {
        // A new tag constant must be given a value shape in TraceTagPolicy, or it silently disappears
        // from both the log and any exporter.
        var tagConstants = typeof(ScribeTelemetry)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Tag", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(tagConstants);
        Assert.All(tagConstants, key => Assert.True(TraceTagPolicy.IsAllowed(key), $"{key} has no policy"));
    }

    [Fact]
    public void The_export_scrub_removes_unknown_tags_and_neutralizes_unsafe_values()
    {
        var tags = new[]
        {
            Tag(ScribeTelemetry.TagFinalChars, 55),
            Tag("http.host", HostLike),
            Tag(ScribeTelemetry.TagAiSkipReason, $"AI cleanup is enabled but Ready ('llama3' at {HostLike} ready.)."),
            Tag("scribe.transcript", TranscriptLike),
        };

        var plan = TraceTagPolicy.PlanExportScrub(tags);

        Assert.Equal(
            [
                Tag("http.host", null),
                Tag(ScribeTelemetry.TagAiSkipReason, TraceTagPolicy.OmittedValue),
                Tag("scribe.transcript", null),
                Tag(TraceTagPolicy.OmittedTagCountTag, 2),
            ],
            plan);
    }

    [Fact]
    public void A_span_with_only_safe_tags_needs_no_export_changes()
    {
        var plan = TraceTagPolicy.PlanExportScrub([
            Tag(ScribeTelemetry.TagOutcome, DictationOutcome.Injected),
            Tag(ScribeTelemetry.TagInjectComplete, true),
        ]);

        Assert.Empty(plan);
    }

    [Fact]
    public void A_status_description_stays_on_one_line()
    {
        Assert.Equal(": first second", TraceTagPolicy.FormatStatusDetail("first\r\nsecond"));
        Assert.Equal(string.Empty, TraceTagPolicy.FormatStatusDetail(null));
        Assert.Equal(string.Empty, TraceTagPolicy.FormatStatusDetail("  "));
    }
}
