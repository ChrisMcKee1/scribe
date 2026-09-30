using System.Text.Json;
using System.Text.Json.Nodes;
using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

/// <summary>
/// Applies today's answer cleanup (<see cref="TextCleanupService.TrySanitize"/>) to the answers a benchmark already
/// recorded, and writes them as a sibling arm (<c>&lt;folder&gt;-resanitized</c>) for the blind judge. It shows what a
/// change to the cleanup does to thousands of real answers without running a model again, and lists every answer it
/// changed so a rule that removes the dictation's own words shows up. An answer the earlier cleanup rejected was stored
/// as the raw transcript and cannot be recovered, so the effect it measures is a lower bound.
/// </summary>
internal static class Resanitize
{
    public static int Run(IReadOnlyList<string> resultsFiles)
    {
        var totalChanged = 0;
        foreach (var file in resultsFiles)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var casesPath = Path.Combine(folder, "cases.json");
            if (!File.Exists(file) || !File.Exists(casesPath))
            {
                Console.WriteLine($"Skipped {file}: results.json and cases.json are both needed.");
                continue;
            }

            var transcripts = JsonNode.Parse(File.ReadAllText(casesPath))!.AsArray()
                .ToDictionary(c => c!["CaseId"]!.GetValue<string>(), c => c!["Transcript"]!.GetValue<string>());
            var results = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
            var changed = 0;
            var rejected = 0;
            foreach (var model in results)
            {
                foreach (var caseNode in model!["Cases"]?.AsArray() ?? [])
                {
                    var id = caseNode!["CaseId"]!.GetValue<string>();
                    if (!transcripts.TryGetValue(id, out var raw))
                    {
                        continue;
                    }

                    var outputs = caseNode["Outputs"]?.AsArray();
                    var outcomes = caseNode["Outcomes"]?.AsArray();
                    if (outputs is null)
                    {
                        continue;
                    }

                    for (var i = 0; i < outputs.Count; i++)
                    {
                        var before = outputs[i]?.GetValue<string>() ?? string.Empty;
                        if (!TextCleanupService.TrySanitize(before, raw, out var after))
                        {
                            // Rejected now: the user would get the raw transcript.
                            after = raw;
                            rejected++;
                            if (outcomes is not null && i < outcomes.Count)
                            {
                                outcomes[i] = "Failed";
                            }
                        }

                        if (string.Equals(before, after, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        changed++;
                        outputs[i] = after;
                        Console.WriteLine($"  {model["Id"]} {id}#{i}: {Flat(before, 90)}  ->  {Flat(after, 90)}");
                    }

                    caseNode["Output"] = outputs.Count > 0 ? outputs[^1]?.GetValue<string>() : caseNode["Output"]?.GetValue<string>();
                }
            }

            var target = folder + "-resanitized";
            Directory.CreateDirectory(target);
            File.Copy(casesPath, Path.Combine(target, "cases.json"), overwrite: true);
            File.WriteAllText(Path.Combine(target, "results.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{Path.GetFileName(folder)}: {changed} answer(s) changed, {rejected} now rejected -> {target}");
            totalChanged += changed;
        }

        Console.WriteLine($"{totalChanged} answer(s) changed in all.");
        return 0;
    }

    private static string Flat(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" | ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
