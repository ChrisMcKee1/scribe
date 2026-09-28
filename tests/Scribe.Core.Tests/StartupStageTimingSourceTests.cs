using System.Text.RegularExpressions;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// StartupStageTiming's wiring in the WPF shell: the timeline is anchored first thing in Main, every stage code is a valid
/// code used once per timeline, and nothing is logged unless the flag is on.
/// </summary>
public sealed class StartupStageTimingSourceTests
{
    [Fact]
    public void The_startup_timeline_is_anchored_before_anything_else_in_Main()
    {
        var program = Read("src", "Scribe.App", "Program.cs");
        var main = program[program.IndexOf("private static void Main(", StringComparison.Ordinal)..];
        var body = main[(main.IndexOf('{', StringComparison.Ordinal) + 1)..];
        var firstStatement = Regex.Replace(body, @"^\s*(//[^\n]*\n\s*)*", string.Empty);

        Assert.StartsWith("var stages = StageTimeline.Start();", firstStatement, StringComparison.Ordinal);
        Assert.Contains("app.StartupStages = stages;", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_stage_code_is_valid_and_used_once_per_timeline()
    {
        var startup = Codes(Read("src", "Scribe.App", "Program.cs"), "stages").Concat(Codes(Read("src", "Scribe.App", "App.xaml.cs"), "StartupStages?")).ToList();
        startup.Add("started");
        var settingsOpen = Codes(Read("src", "Scribe.App", "App.xaml.cs"), "openStages?")
            .Concat(Codes(Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"), "openStages?"))
            .Append("rendered")
            .ToList();

        Assert.True(startup.Count >= 15, string.Join(",", startup));
        Assert.True(settingsOpen.Count >= 8, string.Join(",", settingsOpen));
        foreach (var codes in new[] { startup, settingsOpen })
        {
            Assert.All(codes, code => Assert.True(StageTimeline.IsCode(code), code));
            Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.True(startup.Count <= StageTimeline.MaxMarks);
        Assert.True(settingsOpen.Count <= StageTimeline.MaxMarks);
    }

    [Fact]
    public void Nothing_is_logged_unless_the_flag_is_on()
    {
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var startup = Slice(app, "private void LogStartupStages(ILogger log, PerfFlags perfFlags)", "private void LogSettingsOpenStagesWhenRendered(");
        Assert.Contains("if (stages is null || !perfFlags.IsOn(PerfFlags.StartupStageTiming))", startup, StringComparison.Ordinal);
        Assert.Contains("\"Startup stages in ms since the process was created: main={Main} {Stages}.\"", startup, StringComparison.Ordinal);
        Assert.Contains("\"Startup stages in ms since Main: {Stages}.\"", startup, StringComparison.Ordinal);

        Assert.Contains(
            "var openStages = services.GetRequiredService<PerfFlags>().IsOn(PerfFlags.StartupStageTiming) ? StageTimeline.Start() : null;",
            app,
            StringComparison.Ordinal);
        var open = Slice(app, "private void LogSettingsOpenStagesWhenRendered(", "private void CopyLastDictation()");
        Assert.Contains("\"Settings window open {Open} stages in ms since it was asked for: {Stages}.\"", open, StringComparison.Ordinal);
        Assert.Contains("window.ContentRendered -= rendered;", open, StringComparison.Ordinal);

        // The one call site, right after the "Scribe started" line, whose own template is untouched.
        Assert.Single(Regex.Matches(app, @"LogStartupStages\(log, services\.GetRequiredService<PerfFlags>\(\)\);"));
        Assert.Contains("\"Scribe started. Dictation hotkey {Key} ({Mode}), dictation-only hotkey {DictationOnlyKey}.\"", app, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Codes(string source, string receiver) =>
        Regex.Matches(source, Regex.Escape(receiver) + @"\.Mark\(""(?<code>[^""]*)""\)").Select(m => m.Groups["code"].Value);

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
