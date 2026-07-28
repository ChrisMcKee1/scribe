using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.InjectionLab;

/// <summary>
/// Measures Scribe's injection paths against a real focused Win32 control so the default method is
/// a measured choice rather than an assumption. Each case is injected into a freshly cleared
/// control, timed end to end, then read back with WM_GETTEXT and compared to the input: latency is
/// meaningless if the text arrived wrong.
/// <para>
/// This is a manual tool, not a unit test. It steals foreground focus and synthesizes keystrokes,
/// so it must own the desktop while it runs.
/// </para>
/// </summary>
internal static class Program
{
    private sealed record Case(string Id, string Text);

    private static readonly Case[] Cases =
    [
        new("short", "Ship the build by Thursday."),
        new("typical", "I need to send the quarterly report to Sarah on the finance team by Friday. " +
                       "Make sure the Q3 revenue numbers are in there, the ones we discussed last week."),
        new("long", string.Join(' ', Enumerable.Repeat(
            "This is a longer dictation that exercises the chunked SendInput path end to end.", 12))),
        new("paragraphs", "First, the release update: the desktop build passed validation.\r\n\r\n" +
                          "Separately, three teams asked for a simpler onboarding guide."),
        new("unicode", "Café résumé naïve — 日本語 — emoji \U0001F600 and symbols ≤ ≥ ±."),
    ];

    private static int Main(string[] args)
    {
        var runs = ArgInt(args, "--runs", 5);
        var rich = args.Contains("--richedit", StringComparer.OrdinalIgnoreCase);
        var custom = args.Contains("--custom", StringComparer.OrdinalIgnoreCase);
        var methods = ParseMethods(args);

        using var target = new TargetWindow(rich, custom);

        Console.WriteLine("Scribe injection lab");
        Console.WriteLine($"  target control : {target.ControlClass}");
        Console.WriteLine($"  runs per case  : {runs}");
        Console.WriteLine($"  methods        : {string.Join(", ", methods.Select(m => m.Label))}");
        Console.WriteLine();
        Console.WriteLine("Taking foreground focus. Do not type until the run finishes.");
        Console.WriteLine();

        var injector = new TextInjector(NullLogger<TextInjector>.Instance);
        var rows = new List<Row>();

        target.Focus();

        foreach (var method in methods)
        {
            foreach (var c in Cases)
            {
                var times = new List<double>(runs);
                var mismatches = 0;
                string? firstMismatch = null;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var plainEnters = 0;
                var shiftEnters = 0;

                for (var i = 0; i < runs; i++)
                {
                    target.Clear();
                    if (!target.EnsureForeground(TimeSpan.FromSeconds(3)))
                    {
                        firstMismatch ??= "could not take foreground; measurement skipped";
                        mismatches++;
                        times.Add(0);
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    InjectionResult result = default!;
                    var work = Task.Run(() => result = injector.Inject(
                        c.Text, method.Method, target.Handle, method.ShiftEnter));

                    // The control only renders while this thread pumps, so pump for the duration.
                    target.PumpUntil(work, TimeSpan.FromSeconds(30));
                    work.GetAwaiter().GetResult();
                    sw.Stop();

                    // Let the last batch's WM_CHARs drain before reading back.
                    target.Pump(TimeSpan.FromMilliseconds(120));
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    paths.Add(result.Method);
                    plainEnters += target.PlainEnters;
                    shiftEnters += target.ShiftEnters;

                    var actual = Normalize(target.ReadText());
                    var expected = Normalize(c.Text);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    {
                        mismatches++;
                        firstMismatch ??= Describe(expected, actual);
                    }

                    if (!result.Succeeded)
                    {
                        firstMismatch ??= result.Error;
                    }
                }

                times.Sort();
                var path = string.Join('+', paths.OrderBy(p => p, StringComparer.Ordinal));
                rows.Add(new Row(
                    method.Label, c.Id, c.Text.Length,
                    times[times.Count / 2], times[0], times[^1],
                    runs - mismatches, runs, path, plainEnters, shiftEnters, firstMismatch));

                var enters = plainEnters + shiftEnters > 0 ? $"  enter plain={plainEnters} shift={shiftEnters}" : "";
                Console.WriteLine(
                    $"  {method.Label,-22} {c.Id,-12} {times[times.Count / 2],8:F1} ms  " +
                    $"exact {runs - mismatches}/{runs}  via {path}{enters}");
            }
        }

        Console.WriteLine();
        Report(rows);
        return rows.Any(r => r.Exact < r.Runs) ? 1 : 0;
    }

    private sealed record Row(
        string Method, string Case, int Chars,
        double MedianMs, double MinMs, double MaxMs,
        int Exact, int Runs, string Path, int PlainEnters, int ShiftEnters, string? Note);

    private static void Report(IReadOnlyList<Row> rows)
    {
        Console.WriteLine("=== Per case ===");
        Console.WriteLine(
            $"{"Method",-22} {"Case",-12} {"Chars",6} {"Median",9} {"Exact",7} {"Path",-12} {"Enter p/s",10}");
        Console.WriteLine(new string('-', 88));
        foreach (var r in rows)
        {
            Console.WriteLine(
                $"{r.Method,-22} {r.Case,-12} {r.Chars,6} {r.MedianMs,8:F1}m " +
                $"{r.Exact,3}/{r.Runs,-3} {r.Path,-12} {r.PlainEnters,4}/{r.ShiftEnters,-5}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Per method ===");
        Console.WriteLine($"{"Method",-22} {"MedianOfMedians",16} {"CharsPerSec",12} {"Exact",8}");
        Console.WriteLine(new string('-', 62));
        foreach (var g in rows.GroupBy(r => r.Method))
        {
            var medians = g.Select(r => r.MedianMs).OrderBy(m => m).ToList();
            var median = medians[medians.Count / 2];
            var chars = g.Sum(r => (double)r.Chars);
            var ms = g.Sum(r => r.MedianMs);
            var exact = g.Sum(r => r.Exact);
            var total = g.Sum(r => r.Runs);
            Console.WriteLine($"{g.Key,-22} {median,15:F1}m {chars / (ms / 1000.0),11:F0} {exact,4}/{total,-4}");
        }

        var notes = rows.Where(r => r.Note is not null).ToList();
        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== Fidelity failures ===");
            foreach (var n in notes)
            {
                Console.WriteLine($"  {n.Method} / {n.Case}: {n.Note}");
            }
        }
    }

    private sealed record MethodArm(string Label, InjectionMethod Method, bool ShiftEnter);

    private static MethodArm[] ParseMethods(string[] args)
    {
        var only = ArgValue(args, "--methods");
        var all = new[]
        {
            new MethodArm("type-shift-enter", InjectionMethod.UnicodeType, true),
            new MethodArm("type-plain-enter", InjectionMethod.UnicodeType, false),
            new MethodArm("paste", InjectionMethod.ClipboardPaste, true),
        };

        if (string.IsNullOrWhiteSpace(only))
        {
            return all;
        }

        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all.Where(a => wanted.Contains(a.Label, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    // EDIT hands back CRLF regardless of what was typed, so compare on a single canonical form.
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private static string Describe(string expected, string actual)
    {
        if (actual.Length == 0)
        {
            return "control was empty after injection";
        }

        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        var expectedTail = expected.Length > i ? Escape(expected[i..Math.Min(expected.Length, i + 24)]) : "(end)";
        var actualTail = actual.Length > i ? Escape(actual[i..Math.Min(actual.Length, i + 24)]) : "(end)";
        return $"diverged at {i} (len {expected.Length} vs {actual.Length}): expected '{expectedTail}', got '{actualTail}'";
    }

    private static string Escape(string s) => s.Replace("\n", "\\n");

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ArgInt(string[] args, string name, int fallback) =>
        int.TryParse(ArgValue(args, name), out var v) && v > 0 ? v : fallback;
}
