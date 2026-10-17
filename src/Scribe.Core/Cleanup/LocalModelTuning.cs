using Scribe.Core.Models;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The tuning a model on this PC runs with, read from the settings of the app that runs it: the context size Scribe asks
/// Ollama or LM Studio for, and whether the whole vocabulary goes when it fits. Each app keeps its own, so switching between
/// Ollama, LM Studio and Foundry Local never carries one app's tuning to another, and any other AI service gets none of it.
/// A size chosen for Ollama also sends its requests through Ollama's own chat API, the only one that takes a size.
/// </summary>
public static class LocalModelTuning
{
    /// <summary><paramref name="options"/> with the tuning <paramref name="settings"/> hold for the app that runs the model.</summary>
    public static CleanupOptions Apply(CleanupOptions options, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        if (options.Provider == CleanupProvider.FoundryLocal)
        {
            return options with
            {
                LocalContextTokens = null,
                SendWholeVocabulary = settings.AiCleanupFoundryLocalSendWholeVocabulary,
                TensorSharpBackend = null,
                TensorSharpReasoningEffort = null,
            };
        }

        return LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) switch
        {
            LocalServerApp.Ollama => options with
            {
                LocalContextTokens = Size(settings.AiCleanupOllamaContextTokens),
                SendWholeVocabulary = settings.AiCleanupOllamaSendWholeVocabulary,
                TensorSharpBackend = null,
                TensorSharpReasoningEffort = null,
            },
            LocalServerApp.LmStudio => options with
            {
                LocalContextTokens = Size(settings.AiCleanupLmStudioContextTokens),
                SendWholeVocabulary = settings.AiCleanupLmStudioSendWholeVocabulary,
                TensorSharpBackend = null,
                TensorSharpReasoningEffort = null,
            },
            LocalServerApp.TensorSharp => options with
            {
                LocalContextTokens = Size(settings.AiCleanupTensorSharpContextTokens),
                SendWholeVocabulary = settings.AiCleanupTensorSharpSendWholeVocabulary,
                TensorSharpBackend = NullIfEmpty(SanitizeTensorSharpBackend(settings.AiCleanupTensorSharpBackend)),
                TensorSharpReasoningEffort = NullIfEmpty(SanitizeTensorSharpReasoningEffort(settings.AiCleanupTensorSharpReasoningEffort)),
            },
            _ => options with
            {
                LocalContextTokens = null,
                SendWholeVocabulary = false,
                TensorSharpBackend = null,
                TensorSharpReasoningEffort = null,
            },
        };
    }

    /// <summary>Known TensorSharp compute backends in the order Settings offers them.</summary>
    public static IReadOnlyList<string> KnownTensorSharpBackends { get; } =
        ["ggml_cuda", "cuda", "ggml_vulkan", "ggml_cpu", "cpu"];

    /// <summary>Known TensorSharp thinking effort levels (<c>low</c>, <c>medium</c>, <c>high</c>).</summary>
    public static IReadOnlyList<string> KnownTensorSharpReasoningEfforts { get; } =
        ["low", "medium", "high"];

    /// <summary>Normalizes a stored TensorSharp backend id; unknown or blank values become <see cref="string.Empty"/> (TensorSharp's setting).</summary>
    public static string SanitizeTensorSharpBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return string.Empty;
        }

        var trimmed = backend.Trim();
        return KnownTensorSharpBackends.FirstOrDefault(known =>
            string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    /// <summary>Normalizes a stored TensorSharp thinking effort; unknown or blank values become <see cref="string.Empty"/> (thinking off).</summary>
    public static string SanitizeTensorSharpReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return string.Empty;
        }

        var trimmed = effort.Trim();
        return KnownTensorSharpReasoningEfforts.FirstOrDefault(known =>
            string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static int? Size(int tokens) => ContextBudget.Sanitize(tokens) is > 0 and var size ? size : null;
}
