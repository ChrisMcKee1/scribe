using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.InjectionLab;

/// <summary>
/// Measures Scribe's injection paths against a real focused Win32 control so the default method is
/// a measured choice rather than an assumption. Each case is injected into a freshly cleared
/// control, timed end to end, then read back and compared to the input: latency is meaningless if
/// the text arrived wrong.
/// <para>
/// A run only counts as a sample of an arm when the injector reports the path that arm exists to
/// measure (typing arms expect "unicode", the paste arm expects "clipboard", the fast-path arm expects
/// "win32-edit"), the text read back is exact, and, for the paste arm, the target itself served exactly
/// one paste from the clipboard, the injector reports the clipboard restored, and the clipboard reads
/// back as it was before the run. A run that could not take the foreground, acquire the clipboard, or
/// keep it from another application is reported as not measured and never contributes a time. Only
/// passing runs contribute to the latency figures.
/// </para>
/// <para>
/// EDIT and RichEdit targets always take the injector's win32-edit fast path, so by default they run
/// only the fast-path arm. The custom target (--custom) is neither, so it is the only target that
/// measures typing and paste, and those are its default arms. --methods picks arms explicitly; an arm
/// the target cannot exercise is reported as a wrong path, never as a sample.
/// </para>
/// <para>
/// This is a manual tool, not a unit test. It steals foreground focus, synthesizes keystrokes and
/// borrows the clipboard, so run it only in a dedicated VM or an isolated interactive desktop that
/// nobody is using, never in someone's working session. Close clipboard managers and remote clipboard
/// sync first, and leave plain text (or nothing) on the clipboard. Exit code 0 means every run passed,
/// 1 means at least one run took the wrong path or failed fidelity or restore, 2 means nothing failed
/// but some runs could not be measured, and 3 means --methods named no known arm.
/// </para>
/// </summary>
internal static class Program
{
    private const string TypingPath = "unicode";
    private const string ClipboardPath = "clipboard";
    private const string EditFastPath = "win32-edit";

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
        var methods = ParseMethods(args, custom);
        if (methods.Length == 0)
        {
            Console.WriteLine($"No arm matched --methods. Known arms: {string.Join(", ", AllArms.Select(a => a.Label))}.");
            return 3;
        }

        using var target = new TargetWindow(rich, custom);

        Console.WriteLine("Scribe injection lab");
        Console.WriteLine($"  target control : {target.ControlClass}");
        Console.WriteLine($"  runs per case  : {runs}");
        Console.WriteLine($"  methods        : {string.Join(", ", methods.Select(m => $"{m.Label} (expects {m.ExpectedPath})"))}");
        Console.WriteLine();
        if (!target.IsCustomTarget)
        {
            Console.WriteLine("Note: EDIT and RichEdit targets always take the injector's win32-edit fast path, so they");
            Console.WriteLine("measure only that path. Use --custom to measure typing and paste.");
            Console.WriteLine();
        }

        Console.WriteLine("Run this only in a dedicated VM or an isolated desktop nobody is using: it takes foreground");
        Console.WriteLine("focus, sends real keystrokes and borrows the clipboard. Do not type until the run finishes.");
        Console.WriteLine();

        var injector = new TextInjector(NullLogger<TextInjector>.Instance);
        var rows = new List<Row>();

        target.Focus();

        foreach (var method in methods)
        {
            foreach (var c in Cases)
            {
                var samples = new List<Sample>(runs);
                for (var i = 0; i < runs; i++)
                {
                    samples.Add(RunOnce(target, injector, method, c));
                }

                var row = Summarize(method, c, samples);
                rows.Add(row);

                var enters = row.PlainEnters + row.ShiftEnters > 0 ? $"  enter plain={row.PlainEnters} shift={row.ShiftEnters}" : "";
                Console.WriteLine(
                    $"  {method.Label,-22} {c.Id,-12} {FormatMs(row.MedianMs),8} ms  " +
                    $"pass {row.Passed}/{row.Measured} measured, {row.NotMeasured} not measured, " +
                    $"{row.WrongPath} wrong path  via {row.Path}{enters}");
            }
        }

        Console.WriteLine();
        Report(rows);
        return ExitCode(rows);
    }

    private static Sample RunOnce(TargetWindow target, TextInjector injector, MethodArm arm, Case c)
    {
        target.Clear();
        if (!target.EnsureForeground(TimeSpan.FromSeconds(3)))
        {
            // Nothing was injected, so there is nothing to time: never a 0 ms sample.
            return Sample.NotMeasured("could not take foreground");
        }

        var pasting = arm.ExpectedPath == ClipboardPath;
        var before = pasting ? target.ReadClipboard() : default;
        if (pasting && !before.Readable)
        {
            return Sample.NotMeasured("clipboard busy before the run");
        }

        if (pasting && !before.Empty && before.Text is null)
        {
            // The injector would rightly refuse to borrow it, so the paste path cannot be exercised.
            return Sample.NotMeasured("clipboard holds non-text content; leave plain text or nothing on it");
        }

        var stopwatch = Stopwatch.StartNew();
        var work = Task.Run(() => injector.Inject(c.Text, arm.Method, target.Handle, arm.ShiftEnter));

        // The target renders keystrokes and serves its paste only while this thread pumps, so pump for
        // as long as the injection takes rather than stopping at a timeout and then blocking on it.
        target.PumpUntil(
            work,
            TimeSpan.FromSeconds(30),
            () => Console.WriteLine($"  {arm.Label} / {c.Id}: still injecting after 30 s; still pumping"));
        var result = work.GetAwaiter().GetResult();
        stopwatch.Stop();

        // Let the last batch's WM_CHARs drain before reading back.
        target.Pump(TimeSpan.FromMilliseconds(120));
        var observed = new Observed(
            Normalize(target.ReadText()),
            target.IsCustomTarget,
            target.Pastes,
            target.PasteFailures,
            target.PlainEnters,
            target.ShiftEnters,
            before,
            pasting ? target.ReadClipboard() : default);

        return Classify(arm, c, result, observed, stopwatch.Elapsed.TotalMilliseconds);
    }

    // What the target and the clipboard showed after a run, independently of what the injector reported.
    private readonly record struct Observed(
        string Text,
        bool CustomTarget,
        int Pastes,
        int PasteFailures,
        int PlainEnters,
        int ShiftEnters,
        TargetWindow.ClipboardView ClipboardBefore,
        TargetWindow.ClipboardView ClipboardAfter);

    private static Sample Classify(MethodArm arm, Case c, InjectionResult result, Observed seen, double ms)
    {
        // Scribe's claims that another application took the clipboard, or that its text could not be
        // read, are checked against what the lab itself saw before they are excused as environment: a
        // false one means Scribe's ownership proof or its read is broken, which is exactly what this
        // lab exists to catch.
        if ((result.Paste == PasteDelivery.Superseded || result.ClipboardRestore == ClipboardRestoreOutcome.Superseded)
            && seen.ClipboardAfter.Readable
            && string.Equals(seen.ClipboardAfter.Text, c.Text, StringComparison.Ordinal))
        {
            return Sample.Failed(
                ms, result.Method, "Scribe reported Superseded while its own text is still on the clipboard",
                seen.PlainEnters, seen.ShiftEnters);
        }

        if (result.Paste == PasteDelivery.SnapshotUnreadable
            && seen.ClipboardBefore.Text is not null
            && seen.ClipboardAfter == seen.ClipboardBefore)
        {
            return Sample.Failed(
                ms, result.Method, "Scribe reported the clipboard text unreadable, but the lab read it and nothing changed it",
                seen.PlainEnters, seen.ShiftEnters);
        }

        // The run never had a clipboard or a foreground to work with, or another application changed
        // the clipboard mid-run: the environment was measured, not the path, so neither the time nor
        // the text counts.
        if (result.Paste is PasteDelivery.ClipboardBusy or PasteDelivery.Unconfirmed or PasteDelivery.NonTextContent
                or PasteDelivery.SnapshotUnreadable or PasteDelivery.Superseded or PasteDelivery.FocusChanged
            || result.ClipboardRestore == ClipboardRestoreOutcome.Superseded)
        {
            return Sample.NotMeasured($"clipboard {result.Paste}, restore {result.ClipboardRestore}", result.Method);
        }

        if (result.Error == InjectionResult.FocusChangedError)
        {
            return Sample.NotMeasured("focus changed during the run", result.Method);
        }

        if (!string.Equals(result.Method, arm.ExpectedPath, StringComparison.Ordinal))
        {
            var reason = result.Paste == PasteDelivery.NotUsed ? "" : $" ({result.Paste})";
            return Sample.WrongPath(result.Method, $"took {result.Method}{reason}, arm expects {arm.ExpectedPath}");
        }

        // The custom target sees every paste itself, so the injector's report of its own path is checked
        // against what actually arrived: one paste for the paste arm, none for a typing arm.
        if (seen.CustomTarget)
        {
            var expectedPastes = arm.ExpectedPath == ClipboardPath ? 1 : 0;
            if (seen.Pastes != expectedPastes || seen.PasteFailures > 0)
            {
                return Sample.Failed(
                    ms, result.Method,
                    $"target served {seen.Pastes} paste(s) and failed {seen.PasteFailures}, arm expects {expectedPastes}",
                    seen.PlainEnters, seen.ShiftEnters);
            }
        }

        var expected = Normalize(c.Text);
        if (!string.Equals(seen.Text, expected, StringComparison.Ordinal))
        {
            // What a paste delivered may be the operator's own clipboard content, so only its shape is shown.
            var note = arm.ExpectedPath == ClipboardPath
                ? DescribeShape(expected, seen.Text, seen.ClipboardBefore)
                : Describe(expected, seen.Text);
            return Sample.Failed(ms, result.Method, note, seen.PlainEnters, seen.ShiftEnters);
        }

        if (!result.Succeeded)
        {
            return Sample.Failed(
                ms, result.Method, result.Error ?? "the injector reported a failure", seen.PlainEnters, seen.ShiftEnters);
        }

        if (arm.ExpectedPath == ClipboardPath)
        {
            // Compared, never printed: the clipboard holds the operator's own content.
            if (result.ClipboardRestore != ClipboardRestoreOutcome.Restored)
            {
                return Sample.Failed(
                    ms, result.Method, $"restore {result.ClipboardRestore}", seen.PlainEnters, seen.ShiftEnters);
            }

            if (!seen.ClipboardAfter.Readable)
            {
                return Sample.NotMeasured("clipboard busy after the run, so the restore could not be verified", result.Method);
            }

            if (seen.ClipboardAfter != seen.ClipboardBefore)
            {
                return Sample.Failed(
                    ms, result.Method, "restore reported Restored, but the clipboard differs from before the run",
                    seen.PlainEnters, seen.ShiftEnters);
            }
        }

        return Sample.Passed(ms, result.Method, seen.PlainEnters, seen.ShiftEnters);
    }

    private enum Verdict
    {
        Passed,
        Failed,
        WrongPath,
        NotMeasured,
    }

    // Ms is kept for runs that took the arm's own path, but only passing runs feed the latency figures:
    // the time of a run whose text arrived wrong describes nothing worth comparing.
    private readonly record struct Sample(
        Verdict Verdict, double? Ms, string Path, string? Note, int PlainEnters, int ShiftEnters)
    {
        public static Sample Passed(double ms, string path, int plainEnters, int shiftEnters) =>
            new(Verdict.Passed, ms, path, null, plainEnters, shiftEnters);

        public static Sample Failed(double ms, string path, string note, int plainEnters, int shiftEnters) =>
            new(Verdict.Failed, ms, path, note, plainEnters, shiftEnters);

        public static Sample WrongPath(string path, string note) =>
            new(Verdict.WrongPath, null, path, note, 0, 0);

        public static Sample NotMeasured(string why, string path = "none") =>
            new(Verdict.NotMeasured, null, path, "not measured: " + why, 0, 0);
    }

    private sealed record Row(
        string Method, string Case, int Chars,
        double? MedianMs, double? MinMs, double? MaxMs,
        int Passed, int Measured, int NotMeasured, int WrongPath, int Runs,
        string Path, int PlainEnters, int ShiftEnters, string? Note);

    private static Row Summarize(MethodArm arm, Case c, List<Sample> samples)
    {
        var times = samples
            .Where(s => s.Verdict == Verdict.Passed)
            .Select(s => s.Ms!.Value)
            .OrderBy(t => t)
            .ToList();
        var path = string.Join('+', samples.Select(s => s.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal));
        return new Row(
            arm.Label, c.Id, c.Text.Length,
            times.Count > 0 ? times[times.Count / 2] : null,
            times.Count > 0 ? times[0] : null,
            times.Count > 0 ? times[^1] : null,
            samples.Count(s => s.Verdict == Verdict.Passed),
            samples.Count(s => s.Verdict is Verdict.Passed or Verdict.Failed),
            samples.Count(s => s.Verdict == Verdict.NotMeasured),
            samples.Count(s => s.Verdict == Verdict.WrongPath),
            samples.Count,
            path,
            samples.Sum(s => s.PlainEnters),
            samples.Sum(s => s.ShiftEnters),
            samples.Select(s => s.Note).FirstOrDefault(n => n is not null));
    }

    private static void Report(IReadOnlyList<Row> rows)
    {
        Console.WriteLine("=== Per case ===");
        Console.WriteLine(
            $"{"Method",-22} {"Case",-12} {"Chars",6} {"Median",9} {"Pass",9} {"NotMeas",8} {"Wrong",6} {"Path",-16} {"Enter p/s",10}");
        Console.WriteLine(new string('-', 106));
        foreach (var r in rows)
        {
            Console.WriteLine(
                $"{r.Method,-22} {r.Case,-12} {r.Chars,6} {FormatMs(r.MedianMs),8}m " +
                $"{r.Passed,4}/{r.Measured,-4} {r.NotMeasured,8} {r.WrongPath,6} {r.Path,-16} {r.PlainEnters,4}/{r.ShiftEnters,-5}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Per method (passing runs only) ===");
        Console.WriteLine($"{"Method",-22} {"MedianOfMedians",16} {"CharsPerSec",12} {"Pass",10}");
        Console.WriteLine(new string('-', 64));
        foreach (var g in rows.GroupBy(r => r.Method))
        {
            var timed = g.Where(r => r.MedianMs is not null).ToList();
            var passed = g.Sum(r => r.Passed);
            var measured = g.Sum(r => r.Measured);
            if (timed.Count == 0)
            {
                Console.WriteLine($"{g.Key,-22} {"n/a",16} {"n/a",12} {passed,4}/{measured,-5}");
                continue;
            }

            var medians = timed.Select(r => r.MedianMs!.Value).OrderBy(m => m).ToList();
            var median = medians[medians.Count / 2];
            var chars = timed.Sum(r => (double)r.Chars);
            var ms = timed.Sum(r => r.MedianMs!.Value);
            Console.WriteLine($"{g.Key,-22} {median,15:F1}m {chars / (ms / 1000.0),11:F0} {passed,4}/{measured,-5}");
        }

        var notes = rows.Where(r => r.Note is not null).ToList();
        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== Failures and unmeasured runs (first per case) ===");
            foreach (var n in notes)
            {
                Console.WriteLine($"  {n.Method} / {n.Case}: {n.Note}");
            }
        }
    }

    // 1: a run took the wrong path or failed fidelity or restore. 2: nothing failed, but not every run
    // was measured, so the result is incomplete rather than clean.
    private static int ExitCode(IReadOnlyList<Row> rows)
    {
        if (rows.Any(r => r.WrongPath > 0 || r.Passed < r.Measured))
        {
            return 1;
        }

        return rows.Any(r => r.NotMeasured > 0 || r.Measured == 0) ? 2 : 0;
    }

    private static string FormatMs(double? ms) => ms is { } value ? $"{value:F1}" : "n/a";

    private sealed record MethodArm(string Label, InjectionMethod Method, bool ShiftEnter, string ExpectedPath);

    // The fast-path arm passes the production default method: should EM_REPLACESEL ever be skipped on a
    // classic edit control, the run then reports the path production would really have taken.
    private static readonly MethodArm[] AllArms =
    [
        new("type-shift-enter", InjectionMethod.UnicodeType, true, TypingPath),
        new("type-plain-enter", InjectionMethod.UnicodeType, false, TypingPath),
        new("paste", InjectionMethod.ClipboardPaste, true, ClipboardPath),
        new("edit-fast-path", InjectionMethod.ClipboardPaste, true, EditFastPath),
    ];

    private static MethodArm[] ParseMethods(string[] args, bool customTarget)
    {
        var only = ArgValue(args, "--methods");
        if (string.IsNullOrWhiteSpace(only))
        {
            // Each target runs only the arms it can actually exercise.
            return AllArms.Where(a => (a.ExpectedPath == EditFastPath) != customTarget).ToArray();
        }

        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return AllArms.Where(a => wanted.Contains(a.Label, StringComparer.OrdinalIgnoreCase)).ToArray();
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

        int i = CommonPrefixLength(expected, actual);
        var expectedTail = expected.Length > i ? Escape(expected[i..Math.Min(expected.Length, i + 24)]) : "(end)";
        var actualTail = actual.Length > i ? Escape(actual[i..Math.Min(actual.Length, i + 24)]) : "(end)";
        return $"diverged at {i} (len {expected.Length} vs {actual.Length}): expected '{expectedTail}', got '{actualTail}'";
    }

    // The paste arm's mismatch, described without echoing any of what arrived.
    private static string DescribeShape(string expected, string actual, TargetWindow.ClipboardView before)
    {
        if (actual.Length == 0)
        {
            return "control was empty after injection";
        }

        if (before.Text is { } previous && string.Equals(actual, Normalize(previous), StringComparison.Ordinal))
        {
            return "the target pasted the clipboard's previous content: the restore ran before the target read the dictation";
        }

        return $"diverged at {CommonPrefixLength(expected, actual)} (len {expected.Length} vs {actual.Length}); " +
               "content withheld because a paste can deliver the operator's own clipboard";
    }

    private static int CommonPrefixLength(string expected, string actual)
    {
        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        return i;
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
