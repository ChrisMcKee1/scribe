using System.Globalization;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class LogLineFormatTests
{
    private static readonly DateTime Timestamp = new(2026, 9, 21, 14, 5, 9, 123);

    [Theory]
    [InlineData("Scribe.App.Dictation.DictationController", "DictationController")]
    [InlineData("Scribe.Trace", "Trace")]
    [InlineData("App", "App")]
    [InlineData("", "")]
    public void Categories_are_shortened_to_their_last_segment(string category, string expected)
    {
        Assert.Equal(expected, LogLineFormat.ShortCategory(category));
    }

    [Fact]
    public void A_line_has_the_shape_both_processes_write()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
            Assert.Equal(
                "14:05:09.123 [Warning] Overlay: pill hidden",
                LogLineFormat.Format(Timestamp, LogLevel.Warning, "Overlay", "pill hidden")));
    }

    [Fact]
    public void An_exception_follows_on_the_next_line()
    {
        var text = LogLineFormat.Format(
            Timestamp, LogLevel.Error, "App", "failed", new InvalidOperationException("boom"));

        Assert.Contains("App: failed" + Environment.NewLine + "System.InvalidOperationException: boom", text);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fi-FI")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void The_redactor_recognizes_the_line_the_writer_produces_in_any_culture(string culture)
    {
        // The time separator follows the culture, so the redaction anchor must not assume ':'.
        WithCulture(CultureInfo.GetCultureInfo(culture), () =>
        {
            var line = LogLineFormat.Format(
                Timestamp,
                LogLevel.Debug,
                LogLineFormat.ShortCategory("Scribe.Core.Transcription.TranscriptionService"),
                "Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"secret words\"");
            var counts = default(LogRedactionCounts);

            var redacted = HistoricalLogRedaction.RedactLine(line, ref counts);

            Assert.DoesNotContain("secret", redacted);
            Assert.Equal(1, counts.Transcripts);
        });
    }

    private static void WithCulture(CultureInfo culture, Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
