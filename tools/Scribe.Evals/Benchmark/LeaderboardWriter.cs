using System.Text;

namespace Scribe.Evals.Benchmark;

internal sealed record BenchCaseMeta(
    string Id, string Source, string Transcript, string Golden, double? AsrMs, double? AudioSeconds);

internal sealed record LeaderboardMeta
{
    public required string GeneratedUtc { get; init; }
    public required string Machine { get; init; }
    public required string InputSource { get; init; }
    public required string WritingStyle { get; init; }
    public required string JudgeModel { get; init; }
    public int Runs { get; init; }
    public required IReadOnlyList<BenchCaseMeta> Cases { get; init; }
}

/// <summary>Renders the benchmark results to a self-contained markdown leaderboard.</summary>
internal static class LeaderboardWriter
{
    private const double RealtimeBudgetMs = 2000; // a cleanup that feels instant after dictation

    public static void Write(string path, LeaderboardMeta meta, IReadOnlyList<BenchResult> results)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Scribe AI Cleanup — Model Leaderboard");
        sb.AppendLine();
        sb.AppendLine("Speed + quality benchmark of every available Foundry model (Azure cloud and Foundry Local)");
        sb.AppendLine($"driving Scribe's **real** cleanup pipeline across {meta.Cases.Count} deliberately hard dictation");
        sb.AppendLine("cases, each graded against a golden reference rewrite.");
        sb.AppendLine();

        // Metadata.
        sb.AppendLine("## Run metadata");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {meta.GeneratedUtc}");
        sb.AppendLine($"- **Machine:** {meta.Machine}");
        sb.AppendLine($"- **Input source:** {meta.InputSource}");
        sb.AppendLine($"- **Quality judge:** {meta.JudgeModel} (graded against per-case golden references)");
        sb.AppendLine($"- **Timed runs per model:** {meta.Runs} per case (medians pool all samples; 1 warmup discarded; latency uncapped)");
        sb.AppendLine($"- **Cases:** {meta.Cases.Count} ({string.Join(", ", meta.Cases.Select(c => c.Id))})");
        sb.AppendLine($"- **Models benchmarked:** {results.Count}");
        sb.AppendLine();

        foreach (var c in meta.Cases)
        {
            var asr = c.AsrMs is { } ms ? $" · ASR {ms:F0} ms{(c.AudioSeconds is { } s ? $", {s:F1}s audio" : "")}" : "";
            sb.AppendLine($"<details><summary><b>Case: {c.Id}</b> ({c.Source}{asr})</summary>");
            sb.AppendLine();
            sb.AppendLine("**Raw transcript (identical for every model):**");
            sb.AppendLine();
            sb.AppendLine("> " + c.Transcript.ReplaceLineEndings(" "));
            sb.AppendLine();
            sb.AppendLine("**Golden reference rewrite:**");
            sb.AppendLine();
            sb.AppendLine("> " + c.Golden.ReplaceLineEndings(" "));
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        sb.AppendLine();
        sb.AppendLine("<details><summary>Writing style applied</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(meta.WritingStyle.Trim());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        var graded = results
            .Where(r => r.Status is "ok" or "degraded")
            .OrderByDescending(r => r.Quality ?? -1)
            .ThenBy(r => r.MedianMs)
            .ToList();

        var failed = results
            .Where(r => r.Status is not ("ok" or "degraded"))
            .OrderBy(r => r.Group)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Recommendations.
        WriteRecommendations(sb, graded);

        // Combined leaderboard.
        sb.AppendLine("## Overall leaderboard (quality, then speed)");
        sb.AppendLine();
        WriteBoard(sb, graded, withGroup: true);

        // Per-group tables.
        sb.AppendLine("## Cloud (Microsoft Foundry / Azure)");
        sb.AppendLine();
        WriteBoard(sb, graded.Where(r => r.Group == "Cloud").ToList(), withGroup: false);

        sb.AppendLine("## Local (Foundry Local, on-device)");
        sb.AppendLine();
        WriteBoard(sb, graded.Where(r => r.Group == "Local").ToList(), withGroup: false);

        // Speed-only view.
        sb.AppendLine("## Fastest models (median latency)");
        sb.AppendLine();
        var bySpeed = graded.OrderBy(r => r.MedianMs).ToList();
        WriteSpeedBoard(sb, bySpeed);

        // Failures.
        if (failed.Count > 0)
        {
            sb.AppendLine("## Did not produce a gradable result");
            sb.AppendLine();
            sb.AppendLine("| Model | Group | Status | Detail |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in failed)
            {
                sb.AppendLine($"| {Esc(r.Id)} | {r.Group} | {r.Status} | {Esc(Trunc(r.Error ?? "", 160))} |");
            }

            sb.AppendLine();
        }

        // Details: flags, rationale, and verbatim output per graded model.
        sb.AppendLine("## Per-model detail");
        sb.AppendLine();
        foreach (var r in graded)
        {
            sb.AppendLine($"### {r.Id} ({r.Group})");
            sb.AppendLine();
            sb.AppendLine($"- Provider: `{r.Provider}`  ·  target: `{r.Target}`" +
                (r.Note is null ? "" : $"  ·  note: {r.Note}"));
            sb.AppendLine($"- Quality: **{(r.Quality?.ToString() ?? "-")}** ({r.Grade ?? "-"})  ·  " +
                $"median **{r.MedianMs:F0} ms** (min {r.MinMs:F0} / max {r.MaxMs:F0})  ·  changed: {r.Changed}");
            if (r.Dims is { } d)
            {
                sb.AppendLine($"- Dimensions — mechanics {d.Mechanics}, fidelity {d.Fidelity}, " +
                    $"disfluency {d.Disfluency}, instruction {d.Instruction}");
            }

            if (r.Flags.Length > 0)
            {
                sb.AppendLine($"- Flags: {string.Join(", ", r.Flags.Select(f => $"`{f}`"))}");
            }

            if (!string.IsNullOrWhiteSpace(r.Rationale))
            {
                sb.AppendLine($"- Judge: {Esc(r.Rationale!.Trim())}");
            }

            sb.AppendLine();
            if (r.Cases.Length > 0)
            {
                sb.AppendLine("| Case | Quality | Median ms | Changed | Flags |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var c in r.Cases)
                {
                    sb.AppendLine(
                        $"| {c.CaseId} | {(c.Quality?.ToString() ?? "-")} | {c.MedianMs:F0} | " +
                        $"{(c.Changed ? "yes" : "no")} | {Esc(string.Join(", ", c.Flags))} |");
                }

                sb.AppendLine();
                foreach (var c in r.Cases)
                {
                    sb.AppendLine($"<details><summary>{c.CaseId} output" +
                        $"{(string.IsNullOrWhiteSpace(c.Rationale) ? "" : " — " + Esc(Trunc(c.Rationale!, 120)))}</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine((c.Output ?? "").Trim());
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                }

                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("```");
                sb.AppendLine((r.Output ?? "").Trim());
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteRecommendations(StringBuilder sb, IReadOnlyList<BenchResult> graded)
    {
        if (graded.Count == 0)
        {
            return;
        }

        sb.AppendLine("## Recommendations");
        sb.AppendLine();

        var bestCloud = graded.Where(r => r.Group == "Cloud")
            .OrderByDescending(r => r.Quality ?? -1).ThenBy(r => r.MedianMs).FirstOrDefault();
        var bestLocal = graded.Where(r => r.Group == "Local")
            .OrderByDescending(r => r.Quality ?? -1).ThenBy(r => r.MedianMs).FirstOrDefault();
        var bestRealtimeLocal = graded
            .Where(r => r.Group == "Local" && r.MedianMs <= RealtimeBudgetMs && r.Changed)
            .OrderByDescending(r => r.Quality ?? -1).ThenBy(r => r.MedianMs).FirstOrDefault();
        var fastest = graded.Where(r => r.Changed).OrderBy(r => r.MedianMs).FirstOrDefault();

        if (bestCloud is not null)
        {
            sb.AppendLine($"- **Best cloud quality:** `{bestCloud.Id}` — {Q(bestCloud)}, {bestCloud.MedianMs:F0} ms median.");
        }

        if (bestLocal is not null)
        {
            sb.AppendLine($"- **Best local quality:** `{bestLocal.Id}` — {Q(bestLocal)}, {bestLocal.MedianMs:F0} ms median.");
        }

        if (bestRealtimeLocal is not null)
        {
            sb.AppendLine($"- **Best on-device default (≤ {RealtimeBudgetMs:F0} ms, fully offline):** " +
                $"`{bestRealtimeLocal.Id}` — {Q(bestRealtimeLocal)}, {bestRealtimeLocal.MedianMs:F0} ms median. " +
                "Strong quality with real-time feel and no data leaving the machine.");
        }

        if (fastest is not null)
        {
            sb.AppendLine($"- **Fastest overall:** `{fastest.Id}` ({fastest.Group}) — {fastest.MedianMs:F0} ms median, {Q(fastest)}.");
        }

        sb.AppendLine();
    }

    private static void WriteBoard(StringBuilder sb, IReadOnlyList<BenchResult> rows, bool withGroup)
    {
        if (rows.Count == 0)
        {
            sb.AppendLine("_No graded results in this category._");
            sb.AppendLine();
            return;
        }

        sb.Append("| # | Model |");
        if (withGroup)
        {
            sb.Append(" Group |");
        }

        sb.AppendLine(" Quality | Grade | Median ms | Min/Max ms | Changed | Flags | Note |");

        sb.Append("|---|---|");
        if (withGroup)
        {
            sb.Append("---|");
        }

        sb.AppendLine("---|---|---|---|---|---|---|");

        var rank = 0;
        foreach (var r in rows)
        {
            rank++;
            sb.Append($"| {rank} | {Esc(r.Id)} |");
            if (withGroup)
            {
                sb.Append($" {r.Group} |");
            }

            var flags = r.Flags.Length == 0 ? "" : string.Join(", ", r.Flags);
            sb.AppendLine(
                $" {(r.Quality?.ToString() ?? "-")} | {r.Grade ?? "-"} | {r.MedianMs:F0} | " +
                $"{r.MinMs:F0}/{r.MaxMs:F0} | {(r.Changed ? "yes" : "no")} | {Esc(flags)} | {Esc(r.Note ?? "")} |");
        }

        sb.AppendLine();
    }

    private static void WriteSpeedBoard(StringBuilder sb, IReadOnlyList<BenchResult> rows)
    {
        if (rows.Count == 0)
        {
            sb.AppendLine("_No graded results._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| # | Model | Group | Median ms | Quality | Grade |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var rank = 0;
        foreach (var r in rows)
        {
            rank++;
            sb.AppendLine($"| {rank} | {Esc(r.Id)} | {r.Group} | {r.MedianMs:F0} | " +
                $"{(r.Quality?.ToString() ?? "-")} | {r.Grade ?? "-"} |");
        }

        sb.AppendLine();
    }

    private static string Q(BenchResult r) =>
        r.Quality is { } q ? $"quality {q} ({r.Grade})" : "quality n/a";

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
