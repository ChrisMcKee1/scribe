using System.Reflection;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// Contracts between the code that emits diagnostics and the sinks that filter them, checked against the
/// source itself. Reflection over <see cref="ScribeTelemetry"/> alone cannot see a tag set with a string literal
/// or with some other class's constant, which is exactly how the clipboard paste tags once reached the trace
/// with no policy entry; and a sink rule that rewrites a line the controller still writes would silently take
/// the diagnostics it exists to keep.
/// </summary>
public sealed partial class TelemetrySourceContractTests
{
    // Calls whose key is not a constant by design. The export scrub re-applies TraceTagPolicy's own plan to the
    // tags a span already carries, so it can only ever narrow what is shown.
    private static readonly HashSet<(string File, string Key)> PassThroughCalls =
    [
        ("TraceTagScrubProcessor.cs", "change.Key"),
    ];

    [Fact]
    public void Every_tag_the_source_sets_is_keyed_by_a_ScribeTelemetry_constant_with_a_policy_entry()
    {
        var constants = typeof(ScribeTelemetry)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);

        var root = RepositoryRoot();
        var calls = 0;
        var failures = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var source = File.ReadAllText(file);
            foreach (Match call in TagCall().Matches(source))
            {
                calls++;
                var key = call.Groups["key"].Value.Trim();
                var where = $"{Path.GetRelativePath(root, file)}: {call.Value.Trim()}";
                if (PassThroughCalls.Contains((Path.GetFileName(file), key)))
                {
                    continue;
                }

                const string Prefix = nameof(ScribeTelemetry) + ".";
                if (!key.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !constants.TryGetValue(key[Prefix.Length..], out var value))
                {
                    failures.Add($"{where} is not keyed by a {nameof(ScribeTelemetry)} constant");
                }
                else if (!TraceTagPolicy.IsAllowed(value))
                {
                    failures.Add($"{where} sets {value}, which has no {nameof(TraceTagPolicy)} entry");
                }
                else if (value == ScribeTelemetry.TagAiSkipReason && !ValueComesFrom(source, call, nameof(AiSkipReason)))
                {
                    // The one tag that has carried free text before, and a host name with it.
                    failures.Add($"{where} sets the skip reason from something other than {nameof(AiSkipReason)}");
                }
            }
        }

        // The pipeline sets a couple of dozen tags; finding almost none means the scan itself broke.
        Assert.True(calls >= 20, $"Only {calls} tag calls were found under {root}.");
        Assert.Empty(failures);
    }

    [Theory]
    [MemberData(nameof(CleanupStatuses))]
    public void Every_skip_reason_the_controller_can_set_is_shown_as_is(CleanupStatus status)
    {
        var reason = AiSkipReason.NotReady(status);

        Assert.True(TraceTagPolicy.TryFormatValue(ScribeTelemetry.TagAiSkipReason, reason, out var formatted));
        Assert.Equal(reason, formatted);
    }

    [Fact]
    public void The_controllers_cleanup_diagnostics_survive_the_live_sink_redaction()
    {
        // The templates as DictationController writes them; this fails first if they change, so the rendering
        // below is always of the real lines.
        var controller = File.ReadAllText(ControllerPath());
        const string SkipTemplate = "AI cleanup was skipped for this dictation because {Provider} was {Status}. ";
        const string SkipTail = "The raw transcription was used.";
        const string FailedTemplate = "AI cleanup failed ({Provider}, status {Status}); using raw transcription.";
        Assert.Contains("\"#{Id} " + SkipTemplate + "\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"" + SkipTail + "\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"#{Id} " + FailedTemplate + "\"", controller, StringComparison.Ordinal);

        // And their holes are filled with codes only: the dictation number and two enum names. The cleanup
        // result's reasons are sentences, which the live sink would replace, and its display detail can name
        // the endpoint's host.
        var cleanupLines = LogCallScanner.Find(controller)
            .Where(call => call.Text.Contains("AI cleanup was skipped for this dictation", StringComparison.Ordinal) ||
                call.Text.Contains("AI cleanup failed (", StringComparison.Ordinal))
            .Select(call => string.Join(", ", call.Arguments.Skip(1)))
            .ToList();
        Assert.Equal(
            new[] { "session.Id, settings.AiCleanupProvider, status", "session.Id, settings.AiCleanupProvider, _cleanup.Status" },
            cleanupLines);

        foreach (var provider in Enum.GetNames<CleanupProvider>())
        {
            foreach (var status in Enum.GetNames<CleanupStatus>())
            {
                var skipped = Line("Warning", "DictationController",
                    "#7 " + SkipTemplate.Replace("{Provider}", provider).Replace("{Status}", status) + SkipTail);
                var failed = Line("Warning", "DictationController",
                    "#7 " + FailedTemplate.Replace("{Provider}", provider).Replace("{Status}", status));
                var trace = Line("Information", "Trace",
                    "trace dictation.process ai_cleanup=True ai_outcome=Skipped ai_changed=False " +
                    $"ai_skip_reason={AiSkipReason.NotReady(Enum.Parse<CleanupStatus>(status))} final_chars=55 " +
                    "outcome=injected (1234ms)");

                foreach (var line in new[] { skipped, failed, trace })
                {
                    var counts = default(LogRedactionCounts);
                    Assert.Equal(line, HistoricalLogRedaction.RedactLine(line, ref counts));
                    Assert.Equal(default, counts);

                    // What the daily log file itself applies to every entry on its way in.
                    Assert.Equal(line, HistoricalLogRedaction.RedactEntry(line));
                }
            }
        }
    }

    [Fact]
    public void The_controller_hands_the_cleanup_display_detail_to_the_local_failure_log_only()
    {
        // A cleanup result's display detail can name the endpoint's host or quote the endpoint's own error.
        // The controller may pass it to the Settings failure log, which lives in the local database and is
        // never exported, and to nothing else: not a log line, not a tag, and not the overlay, whose log
        // records what it is shown. The overlay gets the diagnostics-safe reason instead.
        var controller = File.ReadAllText(ControllerPath());
        var holders = DisplayDetailHolder().Matches(controller).Select(match => match.Groups["name"].Value).ToList();

        // One for a failed cleanup and one for a partially degraded one, and no other use of the detail.
        Assert.Equal(2, holders.Count);
        Assert.Equal(holders.Count, Regex.Matches(controller, @"\bDisplayDetail\b").Count);

        var recorded = CallsTo(controller, "RecordCleanupFailure").ToList();
        Assert.Equal(2, recorded.Count);
        foreach (var name in holders)
        {
            // Its declaration, and the one RecordCleanupFailure argument.
            Assert.Equal(2, Regex.Matches(controller, $@"\b{name}\b").Count);
            Assert.Single(recorded, arguments => arguments.Contains(name));
        }

        var forbidden = new[] { "DisplayDetail", "FailureReason", "SkipReason" }.Concat(holders).ToArray();
        var offences = new List<string>();
        foreach (var call in LogCallScanner.Find(controller))
        {
            offences.AddRange(Mentions(call.Arguments, forbidden).Select(name => $"a log call passes {name}"));
        }

        foreach (Match tag in TagCall().Matches(controller))
        {
            if (LogCallScanner.TryReadArguments(controller, controller.IndexOf('(', tag.Index), out var arguments, out _))
            {
                offences.AddRange(Mentions(arguments, forbidden).Select(name => $"a tag is set from {name}"));
            }
        }

        foreach (var arguments in CallsTo(controller, "RaiseCleanupFailed"))
        {
            offences.AddRange(Mentions(arguments, ["DisplayDetail", .. holders])
                .Select(name => $"the overlay is sent {name}"));
        }

        Assert.Empty(offences);
    }

    public static TheoryData<CleanupStatus> CleanupStatuses()
    {
        var data = new TheoryData<CleanupStatus>();
        foreach (var status in Enum.GetValues<CleanupStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    private static string Line(string level, string category, string message) =>
        $"14:05:10.456 [{level}] {category}: {message}";

    private static string ControllerPath() =>
        Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs");

    // The top-level arguments of every call to the named method, its declaration excluded.
    private static IEnumerable<List<string>> CallsTo(string source, string method)
    {
        foreach (Match call in Regex.Matches(source, $@"(?<!void )\b{method}\("))
        {
            if (LogCallScanner.TryReadArguments(source, call.Index + call.Length - 1, out var arguments, out _))
            {
                yield return arguments;
            }
        }
    }

    // Which of the names the arguments use as code; plain string literals are only text, so they are ignored.
    private static IEnumerable<string> Mentions(IEnumerable<string> arguments, IReadOnlyCollection<string> names)
    {
        foreach (var argument in arguments)
        {
            var code = PlainStringLiteral().Replace(argument, "\"\"");
            foreach (var name in names.Where(name => Regex.IsMatch(code, $@"\b{name}\b")))
            {
                yield return name;
            }
        }
    }

    // True when the tag's value, the argument right after the key's comma, starts with a member of the named type.
    private static bool ValueComesFrom(string source, Match call, string type) =>
        source.AsSpan(call.Index + call.Length).TrimStart().StartsWith(type + ".", StringComparison.Ordinal);

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

    private static IEnumerable<string> SourceFiles(string root)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) &&
                !file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase));
    }

    // The first argument of every SetTag or AddTag call, up to the comma that ends it. Anything but a plain
    // constant reference (a call, a literal) is captured too, so it is reported rather than skipped.
    [GeneratedRegex(@"\.(?:SetTag|AddTag)\(\s*(?<key>[^,]+?)\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex TagCall();

    [GeneratedRegex(@"\bvar\s+(?<name>[A-Za-z_]\w*)\s*=\s*cleanup\.DisplayDetail\s*\?\?", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayDetailHolder();

    [GeneratedRegex(@"(?<![$@])""(?:[^""\\\r\n]|\\.)*""", RegexOptions.CultureInvariant)]
    private static partial Regex PlainStringLiteral();
}
