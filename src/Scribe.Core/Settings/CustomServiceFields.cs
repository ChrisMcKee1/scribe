using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// The OpenAI-compatible service's fields, as Settings shows and stores them now that "On this PC" can run the AI with
/// Ollama or LM Studio as well as with another AI service set up by hand.
/// </summary>
/// <remarks>
/// One set of fields runs cleanup (<see cref="AppSettings.AiCleanupCustomEndpoint"/>, its model and its key). Ollama and
/// LM Studio are stored there at their own address, with the model from their list and no key, so an older Scribe reads the
/// choice as that server on this PC and keeps working. Another AI service the user had set up is then remembered beside
/// them (<see cref="AppSettings.AiCleanupOtherServiceEndpoint"/>, its model and its key), so choosing it again brings back
/// its address, model and key, as choosing any other option always has.
/// <para>
/// A saved key means another AI service, even at an app's own address: LM Studio can require one, and "On this PC" has no
/// box for it, so that setup stays where its key can be seen and changed, and Save never drops it.
/// </para>
/// </remarks>
public static class CustomServiceFields
{
    /// <summary>A server address, a model name, an API key and the API the service is reached through.</summary>
    public sealed record Fields(
        string? Endpoint, string? Model, string? ApiKey, CustomApiStyle ApiStyle = CustomApiStyle.ChatCompletions)
    {
        public static Fields None { get; } = new(null, null, null);
    }

    /// <summary>Ollama or LM Studio, when <paramref name="settings"/> run the AI with one at its own address and no key.</summary>
    public static LocalServerApp SavedApp(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.IsNullOrWhiteSpace(settings.AiCleanupCustomApiKey)
            ? LocalAiServer.AppServing(settings.AiCleanupProvider, settings.AiCleanupCustomEndpoint)
            : LocalServerApp.None;
    }

    /// <summary>The model <paramref name="settings"/> run Ollama or LM Studio with; null when they run neither.</summary>
    public static string? SavedAppModel(AppSettings settings) =>
        SavedApp(settings) == LocalServerApp.None ? null : Trimmed(settings.AiCleanupCustomModel);

    /// <summary>
    /// What the Another AI service boxes show, as saved: the service that runs cleanup, or the one remembered beside
    /// Ollama or LM Studio.
    /// </summary>
    public static Fields OtherService(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SavedApp(settings) == LocalServerApp.None
            ? new(settings.AiCleanupCustomEndpoint, settings.AiCleanupCustomModel, settings.AiCleanupCustomApiKey, settings.AiCleanupCustomApiStyle)
            : new(
                settings.AiCleanupOtherServiceEndpoint,
                settings.AiCleanupOtherServiceModel,
                settings.AiCleanupOtherServiceApiKey,
                settings.AiCleanupOtherServiceApiStyle);
    }

    /// <summary>
    /// What Save stores: the fields that run cleanup, and the Another AI service boxes to remember beside Ollama or LM
    /// Studio. With <paramref name="app"/> <see cref="LocalServerApp.None"/> the boxes run cleanup and nothing is
    /// remembered. Blank values are stored as null and the rest trimmed, as Settings always stored them. The API stored
    /// for the boxes is the one they reach the service with (<see cref="CustomServiceAddress.Effective"/>: an address that
    /// names its API stores that one), and Ollama and LM Studio store Chat Completions.
    /// </summary>
    /// <param name="app">Ollama or LM Studio when "On this PC" runs the AI with it; otherwise none.</param>
    /// <param name="appModel">The model chosen from the app's list.</param>
    /// <param name="otherService">What the Another AI service boxes hold, with the API chosen for them.</param>
    /// <param name="saved">
    /// The settings last saved: an app keeps the address it was saved at (<c>127.0.0.1</c> or <c>localhost</c>, with or
    /// without a final slash), so opening Settings and saving changes nothing.
    /// </param>
    public static (Fields Stored, Fields Remembered) ForSave(
        LocalServerApp app, string? appModel, Fields otherService, AppSettings saved)
    {
        ArgumentNullException.ThrowIfNull(otherService);
        ArgumentNullException.ThrowIfNull(saved);
        var endpoint = Trimmed(otherService.Endpoint);
        var boxes = new Fields(
            endpoint,
            Trimmed(otherService.Model),
            Trimmed(otherService.ApiKey),
            CustomServiceAddress.Effective(CleanupProvider.OpenAiCompatible, endpoint, otherService.ApiStyle));
        if (app == LocalServerApp.None)
        {
            return (boxes, Fields.None);
        }

        var appEndpoint = SavedApp(saved) == app ? Trimmed(saved.AiCleanupCustomEndpoint) : LocalAiServer.AddressOf(app);
        return (new(appEndpoint, Trimmed(appModel), null), boxes);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
