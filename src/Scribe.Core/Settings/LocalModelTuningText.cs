using System.Globalization;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>
/// What the AI cleanup page says in each app's settings on this PC (<see cref="LocalModelTuning"/>): Ollama's and LM
/// Studio's under their own expander, Foundry Local's under Model settings. Kept in Core beside the behaviour it describes,
/// so a test holds the words to what Scribe does.
/// </summary>
public static class LocalModelTuningText
{
    public const string IdleReleaseTitle = "Free memory when Scribe isn't used";

    public const string IdleReleaseDescription =
        "Choose how long to keep the models ready between dictations. Longer times use more memory and avoid loading " +
        "the model again so often. Never turns off Scribe's idle release. Free memory, pausing and turning AI cleanup " +
        "off can still free the AI model.";

    public static string IdleReleaseHint(LocalServerApp app) =>
        "This is the same choice as Advanced and also controls Scribe's speech models. Save applies it. " +
        IdleReleaseDescription + " " +
        (app == LocalServerApp.Ollama
            ? "With its next request, Scribe asks Ollama to keep the model for this time, or indefinitely for Never. " +
                "Another app or an Ollama restart can still free it."
            : app == LocalServerApp.LmStudio
                ? "LM Studio may still free a model according to its own settings, including with Never."
                : app == LocalServerApp.TensorSharp
                    ? "TensorSharp keeps its hosted model in memory while its server runs."
                    : "The next dictation may take longer after memory is freed.");

    /// <summary>The line under each app's settings expander.</summary>
    public const string TuningSummary =
        "How much the model reads at once, and how much of your vocabulary it gets. Most people never need to change these.";

    /// <summary>The whole vocabulary switch, the same for every app.</summary>
    public const string WholeVocabularyTitle = "Send your whole vocabulary when it fits";

    private const string WholeVocabularyWhat =
        "Sends every word from your dictionary and the word packs you let AI cleanup use, not just the ones a dictation " +
        "seems to mention, when they fit with your dictation. A long list can confuse a small model, ";

    /// <summary>Under the switch for Ollama and LM Studio, which read the words once each time the model loads.</summary>
    public const string AppWholeVocabularyHint =
        WholeVocabularyWhat +
        "and the model reads it again each time it loads: a few seconds with a graphics card, a minute or more without one.";

    /// <summary>Under the switch for Foundry Local, which reads them again for every dictation.</summary>
    public const string FoundryWholeVocabularyHint =
        WholeVocabularyWhat +
        "and Foundry Local reads it again for every dictation, which can add many seconds to each one without a graphics card.";

    /// <summary>The context size choice, the same for Ollama and LM Studio.</summary>
    public const string ContextSizeTitle = "Context size";

    /// <summary>
    /// Under Ollama's context size. A size is asked of Ollama with every request (through its own chat API, the only one
    /// that takes one), and Ollama reloads a model when a request asks for a size other than the one it holds.
    /// </summary>
    public const string OllamaContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. A larger size fits more of your vocabulary " +
        "and can use more memory. With a size chosen, Ollama reloads the model whenever you switch between Scribe and " +
        "another app that uses it at a different size. Ollama's setting leaves it to Context length in Ollama's settings.";

    /// <summary>Under LM Studio's context size.</summary>
    public const string LmStudioContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. With a size chosen, Scribe loads the model " +
        "at it and frees it after the idle time. A model you loaded yourself in LM Studio keeps its own size.";

    /// <summary>Under TensorSharp's context size.</summary>
    public const string TensorSharpContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. Scribe keeps each request within this size " +
        "and within the window TensorSharp loaded the model with. TensorSharp's setting uses the window TensorSharp started with.";

    /// <summary>TensorSharp's compute backend choice.</summary>
    public const string TensorSharpBackendTitle = "How TensorSharp runs the model";

    /// <summary>Under TensorSharp's compute backend choice.</summary>
    public const string TensorSharpBackendHint =
        "Which hardware TensorSharp uses when it loads the model. GGML CUDA is fastest on NVIDIA graphics cards; GGML Vulkan " +
        "works across graphics cards; GGML CPU runs on the processor.";

    /// <summary>TensorSharp's thinking effort choice.</summary>
    public const string TensorSharpReasoningEffortTitle = "Thinking before answering";

    /// <summary>Under TensorSharp's thinking effort choice.</summary>
    public const string TensorSharpReasoningEffortHint =
        "Off asks TensorSharp not to think before answering, which is fastest for dictation. Choose Low, Medium or High " +
        "only for a thinking model that needs extra steps.";

    private static readonly (string Id, string Label)[] DefaultTensorSharpBackends =
    [
        ("ggml_cuda", "GGML CUDA (NVIDIA graphics card)"),
        ("cuda", "Direct CUDA (NVIDIA graphics card)"),
        ("ggml_vulkan", "GGML Vulkan (graphics card)"),
        ("ggml_cpu", "GGML CPU (processor)"),
        ("cpu", "Pure C# CPU (processor)"),
    ];

    /// <summary>
    /// The compute backend choices for TensorSharp: TensorSharp's own setting (<c>""</c>), then the backends TensorSharp
    /// supports (or the known Windows backends before TensorSharp answers). A stored backend is always kept in the list.
    /// </summary>
    public static IReadOnlyList<(string Id, string Label)> TensorSharpBackends(
        IReadOnlyList<(string Id, string Name)>? supported = null, string? stored = null)
    {
        var choices = new List<(string Id, string Label)> { (string.Empty, "TensorSharp's setting") };
        if (supported is { Count: > 0 })
        {
            foreach (var (id, _) in supported)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    choices.Add((id.Trim(), BackendOptionLabel(id.Trim())));
                }
            }
        }
        else
        {
            choices.AddRange(DefaultTensorSharpBackends);
        }

        var sanitizedStored = LocalModelTuning.SanitizeTensorSharpBackend(stored);
        if (sanitizedStored.Length > 0 &&
            !choices.Any(choice => string.Equals(choice.Id, sanitizedStored, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add((sanitizedStored, BackendOptionLabel(sanitizedStored)));
        }

        return choices;
    }

    /// <summary>The thinking effort choices for TensorSharp.</summary>
    public static IReadOnlyList<(string Id, string Label)> TensorSharpReasoningEfforts(string? stored = null)
    {
        var choices = new List<(string Id, string Label)>
        {
            (string.Empty, "Off (fastest)"),
            ("low", "Low"),
            ("medium", "Medium"),
            ("high", "High"),
        };

        var sanitizedStored = LocalModelTuning.SanitizeTensorSharpReasoningEffort(stored);
        if (sanitizedStored.Length > 0 &&
            !choices.Any(choice => string.Equals(choice.Id, sanitizedStored, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add((sanitizedStored, sanitizedStored));
        }

        return choices;
    }

    /// <summary>What Settings calls a TensorSharp backend id in a status line.</summary>
    public static string BackendDisplayName(string? backendId) => backendId?.Trim().ToLowerInvariant() switch
    {
        "ggml_cuda" => "GGML CUDA",
        "cuda" => "Direct CUDA",
        "ggml_vulkan" => "GGML Vulkan",
        "ggml_metal" => "GGML Metal",
        "ggml_cpu" => "GGML CPU",
        "cpu" => "Pure C# CPU",
        { Length: > 0 } other => other,
        _ => "TensorSharp's default",
    };

    private static string BackendOptionLabel(string backendId) =>
        DefaultTensorSharpBackends.FirstOrDefault(known =>
            string.Equals(known.Id, backendId, StringComparison.OrdinalIgnoreCase)).Label
        ?? BackendDisplayName(backendId);

    /// <summary>
    /// The context size choices: the app's own setting (0), then <see cref="ContextBudget.OfferedSizes"/>. A stored size
    /// the list does not offer (one an older or newer build saved) is listed too, so it is shown as it is.
    /// </summary>
    public static IReadOnlyList<(int Tokens, string Label)> ContextSizes(string appName, int stored = 0)
    {
        var sizes = new List<(int Tokens, string Label)> { (0, $"{appName}'s setting") };
        var offered = ContextBudget.OfferedSizes.ToList();
        if (stored > 0 && !offered.Contains(stored))
        {
            offered.Add(stored);
            offered.Sort();
        }

        sizes.AddRange(offered.Select(tokens => (tokens, SizeLabel(tokens))));
        return sizes;
    }

    /// <summary>A size as the list names it: "32K (32,768 tokens)".</summary>
    public static string SizeLabel(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K ({Count(tokens)} tokens)"
            : $"{Count(tokens)} tokens";

    /// <summary>
    /// The status line under an app's settings: the context the model reads, as far as Scribe knows it, how much the whole
    /// vocabulary needs, and whether it fits at that size.
    /// </summary>
    /// <param name="appName">Ollama, LM Studio or Foundry Local.</param>
    /// <param name="inUse">What the app said it loaded the model with; 0 when Scribe does not know.</param>
    /// <param name="asked">The size Scribe asks for; 0 when it leaves the size to the app.</param>
    /// <param name="vocabularyTokens">The whole vocabulary's estimated tokens; 0 when there is none.</param>
    /// <param name="vocabularyRoom">
    /// The tokens of vocabulary that fit beside a typical dictation at the size in use, or asked for
    /// (<see cref="ContextBudget.VocabularyRoom"/>); null when neither is known.
    /// </param>
    public static string ContextStatus(string appName, int inUse, int asked, long vocabularyTokens, int? vocabularyRoom = null)
    {
        var context = inUse > 0
            ? $"The model is reading up to {Count(inUse)} tokens."
            : asked > 0
                ? string.Equals(appName, "TensorSharp", StringComparison.Ordinal)
                    ? $"Scribe keeps requests within {Count(asked)} tokens."
                    : $"Scribe asks for {Count(asked)} tokens when the model loads."
                : $"{appName} sets the size when it loads the model.";
        if (vocabularyTokens <= 0)
        {
            return context + " AI cleanup has no vocabulary to send.";
        }

        var needs = $"{context} Your whole vocabulary needs about {Count(vocabularyTokens)} tokens";
        return vocabularyRoom switch
        {
            null => needs + ".",
            var room when room >= vocabularyTokens => needs + ", which fits at this size.",
            > 0 and var room => $"{needs}; at this size, about {Count(room)} of them fit beside a dictation.",
            _ => needs + "; at this size, none of it fits beside a dictation.",
        };
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
