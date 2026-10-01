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
            };
        }

        return LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) switch
        {
            LocalServerApp.Ollama => options with
            {
                LocalContextTokens = Size(settings.AiCleanupOllamaContextTokens),
                SendWholeVocabulary = settings.AiCleanupOllamaSendWholeVocabulary,
            },
            LocalServerApp.LmStudio => options with
            {
                LocalContextTokens = Size(settings.AiCleanupLmStudioContextTokens),
                SendWholeVocabulary = settings.AiCleanupLmStudioSendWholeVocabulary,
            },
            _ => options with { LocalContextTokens = null, SendWholeVocabulary = false },
        };
    }

    private static int? Size(int tokens) => ContextBudget.Sanitize(tokens) is > 0 and var size ? size : null;
}
