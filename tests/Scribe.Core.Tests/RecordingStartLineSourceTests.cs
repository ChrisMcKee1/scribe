using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The pill shows only once the microphone records, so the time from a press to the pill is mostly the microphone's open
/// time. The slow-open warning names it only above 400 ms, so the recording-started line carries it for every dictation, as
/// a number: a report can then show the whole wait. The controller has no tests of its own (AGENTS.md, "Dictation lifecycle
/// and shutdown"), so this pins the line from source.
/// </summary>
public sealed class RecordingStartLineSourceTests
{
    [Fact]
    public void The_recording_started_line_ends_with_how_long_the_microphone_took_to_open()
    {
        var (template, arguments) = RecordingStartedCall();

        Assert.EndsWith(" cleanup={Cleanup} opened in {OpenMs} ms", template, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(template, Regex.Escape("{OpenMs}")));
        Assert.Equal("(long)open.OpenDuration.TotalMilliseconds", arguments[^1]);
    }

    [Fact]
    public void The_recording_started_line_has_an_argument_for_every_placeholder()
    {
        var (template, arguments) = RecordingStartedCall();

        var placeholders = Regex.Matches(template, @"\{(\w+)(?::[^}]*)?\}").Select(match => match.Groups[1].Value).ToList();
        Assert.Equal("Id", placeholders[0]);
        Assert.Equal("OpenMs", placeholders[^1]);
        Assert.Equal(placeholders.Count, arguments.Count);
        Assert.Equal("id", arguments[0]);
    }

    // The literal template of the controller's "#{Id} recording started" call, and its arguments after it, in order.
    private static (string Template, List<string> Arguments) RecordingStartedCall()
    {
        var controller = ReadSource("src", "Scribe.App", "Dictation", "DictationController.cs").ReplaceLineEndings("\n");
        var start = controller.IndexOf("\"#{Id} recording started: ", StringComparison.Ordinal);
        Assert.True(start > 0, "The recording-started line was not found.");
        Assert.Equal(start, controller.LastIndexOf("\"#{Id} recording started: ", StringComparison.Ordinal));

        var literals = Regex.Match(controller[start..], @"^(?:""(?<part>(?:[^""\\]|\\.)*)""\s*\+?\s*)+");
        Assert.True(literals.Success);
        var template = string.Concat(literals.Groups["part"].Captures.Select(capture => capture.Value));

        var rest = controller[(start + literals.Length)..];
        Assert.StartsWith(",", rest, StringComparison.Ordinal);
        var arguments = new List<string>();
        var depth = 0;
        var from = 1;
        for (var i = 1; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if ((c == ',' && depth == 0) || (c == ')' && depth == 0))
            {
                arguments.Add(rest[from..i].Trim());
                from = i + 1;
                if (c == ')')
                {
                    break;
                }
            }
        }

        return (template, arguments);
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    }
}
