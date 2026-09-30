using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

public sealed record FoundryModelChoice(
    string Alias,
    string Label,
    string Hint,
    bool IsSelected,
    bool IsDownloaded,
    bool IsLoaded);

public static class FoundryModelChoices
{
    public static IReadOnlyList<FoundryModelChoice> Build(
        string? selectedAlias,
        IReadOnlyList<CleanupModel> curated,
        IReadOnlyList<FoundryModelOption> liveCatalog)
    {
        ArgumentNullException.ThrowIfNull(curated);
        ArgumentNullException.ThrowIfNull(liveCatalog);

        var selected = string.IsNullOrWhiteSpace(selectedAlias)
            ? CleanupModelCatalog.DefaultAlias
            : selectedAlias.Trim();
        var liveByAlias = liveCatalog
            .GroupBy(model => model.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var choices = new List<FoundryModelChoice>();

        foreach (var model in curated.OrderBy(ModelSizeBytes))
        {
            liveByAlias.TryGetValue(model.Alias, out var live);
            choices.Add(new FoundryModelChoice(
                model.Alias,
                BuildCuratedLabel(model, live),
                CleanHint(model),
                string.Equals(model.Alias, selected, StringComparison.OrdinalIgnoreCase),
                live?.Cached == true,
                live?.Loaded == true));
        }

        foreach (var live in liveCatalog.Where(live => curated.All(model => !string.Equals(model.Alias, live.Alias, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(live => live.Alias, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(new FoundryModelChoice(
                live.Alias,
                live.Cached ? $"{live.Alias} (downloaded)" : live.Alias,
                "Foundry Local catalog model.",
                string.Equals(live.Alias, selected, StringComparison.OrdinalIgnoreCase),
                live.Cached,
                live.Loaded));
        }

        if (choices.All(choice => !string.Equals(choice.Alias, selected, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new FoundryModelChoice(selected, selected, "Custom Foundry Local model.", true, false, false));
        }

        return choices;
    }

    private static string BuildCuratedLabel(CleanupModel model, FoundryModelOption? live)
    {
        var size = SizePhrase(model);
        var parts = new List<string> { model.DisplayName };
        if (size.Length > 0)
        {
            parts.Add(size);
        }

        if (ModelSizeBytes(model) >= 7_000_000_000)
        {
            parts.Add("large download");
        }

        if (live?.Cached == true)
        {
            parts.Add("downloaded");
        }

        return string.Join(", ", parts);
    }

    // What each model is like, in the curated hint's own words; Settings names no model as the one to pick.
    private static string CleanHint(CleanupModel model) => model.Hint;

    private static string SizePhrase(CleanupModel model)
    {
        var bytes = ModelSizeBytes(model);
        if (bytes <= 0)
        {
            return string.Empty;
        }

        return bytes >= 1_000_000_000
            ? $"about {bytes / 1_000_000_000d:0.#} GB"
            : $"about {bytes / 1_000_000d:0.#} MB";
    }

    // What Foundry Local downloads for each curated model, rounded. Qwen2.5 1.5B is 1.2 GB for NVIDIA RTX graphics,
    // 1.5 GB for other GPUs and 1.8 GB for the CPU; Phi-4 Mini 3.7 GB for NVIDIA graphics (Foundry Local 2.1.0).
    private static long ModelSizeBytes(CleanupModel model) => model.Alias switch
    {
        "qwen2.5-1.5b" => 1_500_000_000,
        "qwen2.5-7b" => 4_700_000_000,
        "qwen3-1.7b" => 1_300_000_000,
        "qwen3-4b" => 2_700_000_000,
        "phi-4-mini" => 3_700_000_000,
        _ => 0,
    };
}
