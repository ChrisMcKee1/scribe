using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>
/// What Settings shows for Ollama or LM Studio under "On this PC": which of the app's models Scribe picks when the user
/// has not chosen one, and the status line under the model.
/// </summary>
public static class LocalAppSetup
{
    /*
     * The order Scribe picks an installed model in when the user has not chosen one: the models that cleaned dictation
     * best for their time on this machine's benchmark (docs/local-model-benchmark.md). Settings recommends none of them;
     * this only fills the list's first choice. Each entry is the model's name reduced to letters and digits, in each
     * app's spelling, because Ollama and LM Studio name the same model differently (gemma4:e2b and google/gemma-4-e2b).
     * With a graphics card that has room for it, Gemma 4 E4B goes first: 89.4 from the blind judge against E2B's 84.0,
     * in 0.6 s against 0.3 s on an RTX 5080. Without one, the smaller model's speed matters more.
     */
    private static readonly string[][] Preference =
    [
        ["gemma4e2b"],
        ["gemma4e4b"],
        ["qwen34binstruct", "qwen34b2507"],
        ["granite43b", "granite4micro"],
        ["gemma34b"],
        ["phi4mini"],
        ["llama323b"],
        ["qwen2515b"],
    ];

    private static readonly string[][] PreferenceWithRoomyGraphicsCard = [Preference[1], Preference[0], .. Preference[2..]];

    /// <summary>
    /// The model to preselect when the user has not chosen one: the first of <paramref name="models"/> in the benchmark's
    /// order for this PC (<paramref name="roomyGraphicsCard"/>: a graphics card with 6 GB or more of its own memory), else
    /// the first model the app lists. Null when it lists none.
    /// </summary>
    public static string? PickModel(IReadOnlyList<LocalServerModel> models, bool roomyGraphicsCard = false)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            return null;
        }

        foreach (var spellings in roomyGraphicsCard ? PreferenceWithRoomyGraphicsCard : Preference)
        {
            foreach (var model in models)
            {
                var key = Key(model.Id);
                if (spellings.Any(spelling => key.Contains(spelling, StringComparison.Ordinal)))
                {
                    return model.Id;
                }
            }
        }

        return models[0].Id;
    }

    /// <summary>
    /// The models the list offers and the one it shows. A model the user chose or saved stays chosen, spelled as it was,
    /// whether or not the app lists it: a name saved without Ollama's <c>:latest</c> is the same model, an LM Studio
    /// instance loaded under a name of its own answers to that name, and a model the app no longer lists stays until the
    /// user picks another, so opening Settings and saving never swaps it. Only with nothing chosen does Scribe preselect
    /// (<see cref="PickModel"/>).
    /// </summary>
    public static (IReadOnlyList<LocalServerModel> Models, string? Selected) ModelChoices(
        IReadOnlyList<LocalServerModel> listed, string? chosen, bool roomyGraphicsCard = false)
    {
        ArgumentNullException.ThrowIfNull(listed);
        var models = listed.ToList();
        var selected = string.IsNullOrWhiteSpace(chosen) ? PickModel(listed, roomyGraphicsCard) : chosen.Trim();
        if (selected is null)
        {
            return (models, null);
        }

        var index = models.FindIndex(model => LocalServerClient.SameModel(model.Id, selected));
        if (index < 0)
        {
            models.Insert(0, new LocalServerModel(selected, selected, 0));
        }
        else if (!string.Equals(models[index].Id, selected, StringComparison.Ordinal))
        {
            var model = models[index];
            models[index] = model with
            {
                Id = selected,
                DisplayName = string.Equals(model.DisplayName, model.Id, StringComparison.Ordinal) ? selected : model.DisplayName,
            };
        }

        return (models, selected);
    }

    /// <summary>
    /// The status line under the model: whether the app answered, whether it has models, and what the chosen model holds
    /// in memory, with the one action that fits. <paramref name="state"/> null means the app is being asked right now.
    /// </summary>
    public static AiCleanupStatusRow Describe(LocalServerApp app, LocalServerState? state, string? model, int idleMinutes)
    {
        var name = AiCleanupPageState.LocalAppName(app);
        var checkAgain = new AiCleanupAction(AiCleanupActionId.CheckAgain, "Check again");
        if (state is null)
        {
            return new(AiCleanupStatusKind.Busy, $"Checking {name}...");
        }

        switch (state.Reach)
        {
            case LocalServerReach.NotRunning:
                return new(AiCleanupStatusKind.Warning, $"Scribe can't reach {name}. Open {name}, then choose Check again.", checkAgain);
            case LocalServerReach.Failed:
                return new(AiCleanupStatusKind.Warning, $"{name} didn't answer. Make sure it's open and up to date, then choose Check again.", checkAgain);
            case LocalServerReach.NeedsKey:
                return new(
                    AiCleanupStatusKind.Warning,
                    $"{name} asks for an API key. To use one, choose Another AI service and enter the key there.",
                    checkAgain);
        }

        if (state.Models.Count == 0)
        {
            return new(AiCleanupStatusKind.Info, $"{name} has no models yet. Download one in {name}, then choose Check again.", checkAgain);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new(AiCleanupStatusKind.Info, "Choose a model.");
        }

        if (state.LoadedFor(model) is { } loaded)
        {
            var memory = loaded.MemoryBytes > 0 ? $"{FormatSize(loaded.MemoryBytes)} of memory" : "memory";
            var freed = idleMinutes > 0
                ? $" Scribe frees it after {idleMinutes} {(idleMinutes == 1 ? "minute" : "minutes")} without a dictation."
                : string.Empty;
            return new(
                AiCleanupStatusKind.Success,
                $"{model} is using {memory}.{freed}",
                new(AiCleanupActionId.Unload, FoundryLocalSetup.FreeMemoryAction));
        }

        if (!state.Models.Any(listed => LocalServerClient.SameModel(listed.Id, model)))
        {
            return new(
                AiCleanupStatusKind.Warning,
                $"{name} doesn't list {model}. Choose another model, or download it in {name}.",
                checkAgain);
        }

        return new(AiCleanupStatusKind.Info, $"{model} isn't using memory now. It loads when you dictate.");
    }

    /// <summary>A size in memory, as Task Manager counts it: "1.6 GB", or "850 MB" below one.</summary>
    public static string FormatSize(long bytes)
    {
        const double Gb = 1024d * 1024 * 1024;
        const double Mb = 1024d * 1024;
        return bytes >= Gb
            ? $"{bytes / Gb:0.0} GB"
            : $"{Math.Max(1, Math.Round(bytes / Mb)):0} MB";
    }

    private static string Key(string name) =>
        new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
