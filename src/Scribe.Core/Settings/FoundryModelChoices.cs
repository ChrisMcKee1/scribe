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
        var name = model.DisplayName.Replace(" (recommended)", string.Empty, StringComparison.Ordinal);
        var parts = new List<string> { name };
        if (size.Length > 0)
        {
            parts.Add(size);
        }

        if (string.Equals(model.Alias, CleanupModelCatalog.DefaultAlias, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("recommended");
        }
        else if (ModelSizeBytes(model) >= 7_000_000_000)
        {
            parts.Add("large download");
        }

        if (live?.Cached == true)
        {
            parts.Add("downloaded");
        }

        return $"{parts[0]}, {string.Join(", ", parts.Skip(1))}";
    }

    private static string CleanHint(CleanupModel model)
    {
        var size = SizePhrase(model);
        return string.Equals(model.Alias, CleanupModelCatalog.DefaultAlias, StringComparison.OrdinalIgnoreCase)
            ? $"{Capitalize(size)}. Scribe's recommended default."
            : $"{Capitalize(size)}.";
    }

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

    private static long ModelSizeBytes(CleanupModel model) => model.Alias switch
    {
        "qwen3-1.7b" => 1_300_000_000,
        "qwen2.5-1.5b" => 1_300_000_000,
        "qwen3.5-2b-text" => 1_400_000_000,
        "qwen3-4b" => 2_700_000_000,
        "phi-4-mini" => 3_600_000_000,
        "mistral-nemo-12b-instruct" => 7_000_000_000,
        "phi-4" => 9_000_000_000,
        _ => 0,
    };

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
