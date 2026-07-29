using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.AsrCheck;

/// <summary>
/// Decodes known speech fixtures through the real transcription stack and asserts the result.
///
/// This exists because the unit tests deliberately do not touch the native ASR engine, which leaves
/// the riskiest part of a multi-architecture build unverified: whether sherpa-onnx and its bundled
/// ONNX Runtime actually load and decode on the silicon we just built for. A build can succeed,
/// publish clean binaries, and still fail on first launch if the wrong native was packaged. Running
/// this on an Arm64 runner turns that into a CI failure rather than a user's crash report.
/// </summary>
internal static class Program
{
    // A correct decode of a clean TTS phrase overlaps almost entirely. This threshold tolerates a
    // synthetic voice fumbling the odd word while still failing loudly on a broken native, which
    // produces empty output or noise rather than most of the right words.
    private const double MinimumWordOverlap = 0.6;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // A native load failure surfaces here as a DllNotFoundException or BadImageFormatException.
            // Those are exactly the architecture regressions this tool exists to catch, so report the
            // type explicitly instead of letting the runtime print a bare stack trace.
            Console.Error.WriteLine($"ASR check failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static int Run(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var fixtureDir = ArgValue(args, "--fixtures")
            ?? Path.Combine(repoRoot ?? AppContext.BaseDirectory, "artifacts", "asr-fixtures");

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Scribe ASR check");
        Console.WriteLine($"  {ComputeCapabilityReport.Detect().Describe()}");
        Console.WriteLine($"  process={RuntimeInformation.ProcessArchitecture} os={RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"  fixtures={fixtureDir}");
        Console.WriteLine();

        var fixtures = LoadFixtures(fixtureDir);
        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine("No fixtures found. Run scripts/New-SpeechFixtures.ps1 first.");
            return 2;
        }

        using var service = new TranscriptionService(
            new ModelLocator(new AppPaths()),
            Options.Create(new TranscriptionOptions()),
            NullLogger<TranscriptionService>.Instance);

        var load = Stopwatch.StartNew();
        service.Initialize();
        load.Stop();
        Console.WriteLine($"Model loaded in {load.ElapsedMilliseconds} ms");
        Console.WriteLine();

        var failures = 0;
        foreach (var fixture in fixtures)
        {
            var samples = WavReader.ReadMonoFloat(fixture.Path, out var sampleRate);
            var result = service.Transcribe(new CapturedAudio(samples, sampleRate));

            var overlap = WordOverlap(fixture.Text, result.Text);
            var ok = overlap >= MinimumWordOverlap;
            if (!ok) failures++;

            // RealTimeFactor is decode/audio, so invert it into the "x realtime" figure the
            // diagnostics panel and the model leaderboard both quote.
            var pace = result.RealTimeFactor > 0 ? 1 / result.RealTimeFactor : 0;

            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {fixture.Name}");
            Console.WriteLine($"       expected: {fixture.Text}");
            Console.WriteLine($"       actual  : {result.Text}");
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "       overlap {0:P0} | {1:0.0}s audio decoded in {2:0} ms ({3:0.0}x realtime)",
                overlap,
                result.AudioDuration.TotalSeconds,
                result.DecodeDuration.TotalMilliseconds,
                pace));
            Console.WriteLine();
        }

        Console.WriteLine(failures == 0
            ? $"All {fixtures.Count} fixtures decoded correctly."
            : $"{failures} of {fixtures.Count} fixtures failed.");

        return failures == 0 ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Walks up from the binary to the repo root so the tool works via `dotnet run`.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Scribe.slnx"))) return dir.FullName;
        }

        return null;
    }

    private static List<Fixture> LoadFixtures(string directory)
    {
        var manifestPath = Path.Combine(directory, "fixtures.json");
        if (!File.Exists(manifestPath)) return [];

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var fixtures = new List<Fixture>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var file = entry.Value.GetProperty("file").GetString()!;
            var text = entry.Value.GetProperty("text").GetString()!;
            fixtures.Add(new Fixture(entry.Name, Path.Combine(directory, file), text));
        }

        return fixtures;
    }

    /// <summary>
    /// Fraction of expected words that appear in the transcript. Deliberately order- and
    /// punctuation-insensitive: the pipeline is allowed to punctuate differently from the prompt,
    /// and this check asks "did the engine decode speech", not "how accurate is it".
    /// </summary>
    internal static double WordOverlap(string expected, string actual)
    {
        var expectedWords = Tokenize(expected);
        if (expectedWords.Count == 0) return 0;

        var actualWords = Tokenize(actual).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedWords.Count(actualWords.Contains) / (double)expectedWords.Count;
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

    private sealed record Fixture(string Name, string Path, string Text);
}
