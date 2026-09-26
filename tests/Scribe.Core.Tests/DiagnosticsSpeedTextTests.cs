using Scribe.Core.Diagnostics;
using Scribe.Core.Transcription;

namespace Scribe.Core.Tests;

public sealed class DiagnosticsSpeedTextTests
{
    [Fact]
    public void State_text_distinguishes_reading_empty_and_failure()
    {
        var reading = DiagnosticsSpeedText.ForState(DiagnosticsSpeedReadState.Reading);
        var empty = DiagnosticsSpeedText.ForState(DiagnosticsSpeedReadState.Empty);
        var failure = DiagnosticsSpeedText.ForState(DiagnosticsSpeedReadState.Failure);

        Assert.Equal("Reading your statistics...", reading.Description);
        Assert.False(reading.ShowRetry);
        Assert.Equal("No dictations in the last 7 days.", empty.Description);
        Assert.False(empty.ShowRetry);
        Assert.Equal("Couldn't read the statistics.", failure.Description);
        Assert.True(failure.ShowRetry);
    }

    [Fact]
    public void Data_text_names_the_speech_model_and_formats_the_table()
    {
        var text = DiagnosticsSpeedText.ForStats(Snapshot(
            new(550, 412, 1_300, 412, 935),
            new(1_200, 900, 1_500, 900, 1_500),
            new(1_750, 1_312, 2_800, 1_312, 2_435)));

        Assert.Equal("Speech model: Parakeet TDT 0.6B v3.", text.Description);
        Assert.True(text.ShowTable);
        Assert.True(text.ShowDetails);
        Assert.Equal("412 ms", text.TypicalSpeechRecognition);
        Assert.Equal("900 ms", text.TypicalCleanup);
        Assert.Equal("1.3 s", text.TypicalCombined);
        Assert.Equal("935 ms", text.SlowSpeechRecognition);
        Assert.Equal("1.5 s", text.SlowCleanup);
        Assert.Equal("2.4 s", text.SlowCombined);
        Assert.Equal("From 2 dictations in the last 7 days.", text.SampleCountLine);
        Assert.Equal("Best: 13 times faster than real time.", text.BestLine);
    }

    [Fact]
    public void Data_text_notes_other_models_and_the_read_limit()
    {
        var text = DiagnosticsSpeedText.ForStats(Snapshot(
            new(550, 412, 1_300, 412, 935),
            cleanup: null,
            combined: null,
            hasOtherModels: true,
            reachedReadLimit: true,
            count: 1));

        Assert.Equal(
            "Speech model: Parakeet TDT 0.6B v3. Earlier dictations with another speech model aren't counted.",
            text.Description);
        Assert.Equal("From 1 dictation in the last 7 days.", text.SampleCountLine);
        Assert.True(text.ShowCapLine);
        Assert.Equal("Only your latest 1,000 dictations are counted.", text.CapLine);
        Assert.Equal("None yet", text.TypicalCleanup);
        Assert.False(text.Cleanup.HasData);
        Assert.Equal("No AI cleanup runs in this period yet.", text.Cleanup.EmptyLine);
    }

    [Fact]
    public void Exclusion_only_snapshot_names_the_model_and_distinguishes_it_from_empty_history()
    {
        var text = DiagnosticsSpeedText.ForStats(Snapshot(
            speech: null,
            cleanup: null,
            combined: null,
            hasOtherModels: true,
            count: 0));

        Assert.Equal(
            "Speech model: Parakeet TDT 0.6B v3. Earlier dictations with another speech model aren't counted. " +
            "No dictations with this speech model in the last 7 days.",
            text.Description);
        Assert.False(text.ShowRetry);
        Assert.False(text.ShowTable);
        Assert.False(text.ShowDetails);
    }

    [Fact]
    public void Exclusion_only_snapshot_at_the_read_limit_still_says_so()
    {
        // The details holding the cap line are hidden when nothing qualifies, so the description carries it.
        var text = DiagnosticsSpeedText.ForStats(Snapshot(
            speech: null,
            cleanup: null,
            combined: null,
            hasOtherModels: true,
            reachedReadLimit: true,
            count: 0));

        Assert.EndsWith(" " + DiagnosticsSpeedText.CapLine, text.Description, StringComparison.Ordinal);
        Assert.False(text.ShowDetails);
    }

    [Fact]
    public void The_saved_model_counts_as_running_only_when_it_is_the_session_s_model()
    {
        // A model saved without a restart is installed but not what recognition runs.
        var window = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var check = window[window.IndexOf("private bool SelectedSpeechModelIsRunning(", StringComparison.Ordinal)..];
        check = check[..check.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("_runningTranscription.ModelId", check, StringComparison.Ordinal);
        Assert.Contains("return available && string.Equals(selected.Id, running.Id, StringComparison.OrdinalIgnoreCase);", check, StringComparison.Ordinal);
    }

    [Fact]
    public void Speed_section_source_avoids_old_overclaiming_words()
    {
        var settings = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var code = File.ReadAllText(Path.Combine(settings, "SettingsWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(settings, "SettingsWindow.xaml"));
        var method = ExtractMethod(code, "private void ShowPerformanceStats");
        var speedXaml = ExtractBetween(xaml, "<TextBlock Text=\"Speed\" Style=\"{StaticResource SectionHeader}\"/>", "<TextBlock Text=\"This PC\"");
        string[] forbidden =
        [
            "Parakeet",
            "pace",
            "rhythm",
            "end to end",
            "total wait",
            "the wait you feel",
        ];

        foreach (var phrase in forbidden)
        {
            Assert.DoesNotContain(phrase, method, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(phrase, speedXaml, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Saving_committed_settings_restarts_the_diagnostics_speed_read()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.History.cs"));
        var method = ExtractMethod(source, "private void OnCommittedSettingsChanged");

        Assert.Contains("LoadPerformanceStats();", method, StringComparison.Ordinal);
    }

    private static DictationStats.Snapshot Snapshot(
        DictationStats.MetricSummary? speech,
        DictationStats.MetricSummary? cleanup,
        DictationStats.MetricSummary? combined,
        bool hasOtherModels = false,
        bool reachedReadLimit = false,
        int count = 2) =>
        new(
            Count: count,
            TotalAudio: TimeSpan.FromSeconds(20),
            CurrentModelId: TranscriptionModelCatalog.DefaultId,
            CurrentModelName: TranscriptionModelCatalog.Resolve(TranscriptionModelCatalog.DefaultId).DisplayName,
            HasEarlierModelDictations: hasOtherModels,
            ReachedReadLimit: reachedReadLimit,
            SpeechRecognitionCount: speech is null ? 0 : count,
            SpeechRecognitionMs: speech,
            CleanupCount: cleanup is null ? 0 : count,
            CleanupMs: cleanup,
            CombinedCount: combined is null ? 0 : count,
            CombinedMs: combined,
            FastestRtf: 0.08,
            RtfP50: 0.10,
            RtfP95: 0.20,
            LongestAudioSeconds: 10);

    private static string ExtractMethod(string source, string name)
    {
        var start = source.IndexOf(name, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0);
        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[brace..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException("Method was not closed.");
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0);
        return source[start..end];
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
