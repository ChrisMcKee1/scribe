using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Overlay.Logging;

// A child process for AppendOnlyLogTests (DATA-O-02). Arguments:
//   <app|overlay> <append-only|old> <logs directory> <yyyy-MM-dd> <tag> <count> <start event name>
// Prints "ready" once it holds the start event, waits for it, then writes <count> numbered lines through the real writer.
//
// And the fixtures of BoundedChildProcessTests (DATA-IMPL-A-06), headless too:
//   fixture hold-stdout   prints "ready", then keeps both streams open doing nothing, for at most a minute
//   fixture flood-stderr  writes 4 MB to standard error, far past a pipe's buffer, then "done" to standard output
if (args is ["ollama-fixture", var role, var mapping, var readyEvent])
{
    return OllamaProcessFixture.Run(role, mapping, readyEvent);
}

if (args is ["fixture", var fixture])
{
    return Fixture.Run(fixture);
}

if (args.Length != 7)
{
    Console.Error.WriteLine("usage: <app|overlay> <append-only|old> <directory> <yyyy-MM-dd> <tag> <count> <event>");
    return 2;
}

var writer = args[0];
var appendOnly = args[1] == "append-only";
var directory = args[2];
var day = DateOnly.ParseExact(args[3], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
var tag = args[4];
var count = int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
using var start = EventWaitHandle.OpenExisting(args[6]);

Console.WriteLine("ready");
Console.Out.Flush();
if (!start.WaitOne(TimeSpan.FromSeconds(30)))
{
    return 3;
}

var clock = day.ToDateTime(new TimeOnly(12, 0));
if (writer == "app")
{
    var file = DailyLogFile.Open(
        directory, null, long.MaxValue, retryDelay: TimeSpan.FromMilliseconds(1), clock: () => clock,
        appendMode: AppendOnlyLogMode.Fixed(appendOnly));
    for (var i = 0; i < count; i++)
    {
        file.Write([new LogRecord(clock, LogLevel.Information, ChildLine.Format(tag, i))]);
    }
}
else
{
    var path = ScribeLogFiles.PathFor(directory, day);
    for (var i = 0; i < count; i++)
    {
        OverlayLog.AppendLineForTests(path, ChildLine.Format(tag, i), appendOnly);
    }
}

return 0;

internal static class ChildLine
{
    // A sequence id and a body whose length and characters vary with the id, some of them outside ASCII, so a torn or
    // overwritten line can never pass for a whole one. AppendOnlyLogTests builds the same lines to compare.
    internal static string Format(string tag, int i) =>
        $"{tag} {i:D6} " + new string((char)('a' + (i % 26)), 40 + (i * 37 % 120)) + (i % 7 == 0 ? " \u00e9\u4e2d\U0001F600" : string.Empty);
}

internal static class Fixture
{
    // What flood-stderr writes, in characters: a pipe's buffer is a few kilobytes.
    internal const int FloodChars = 4 * 1024 * 1024;

    internal static int Run(string name)
    {
        switch (name)
        {
            case "hold-stdout":
                Console.WriteLine("ready");
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromMinutes(1));
                return 0;
            case "flood-stderr":
                var chunk = new string('e', 4096);
                for (var written = 0; written < FloodChars; written += chunk.Length)
                {
                    Console.Error.Write(chunk);
                }

                Console.Error.Flush();
                Console.WriteLine("done");
                Console.Out.Flush();
                return 0;
            default:
                Console.Error.WriteLine("fixtures: hold-stdout, flood-stderr");
                return 2;
        }
    }
}
