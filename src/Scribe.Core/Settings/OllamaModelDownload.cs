using System.Net;
using System.Text.RegularExpressions;
using OllamaSharp.Models.Exceptions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public sealed record OllamaDownloadRequest(string? Model, string? Error);

/// <summary>The download form's validation, notices and narrowly scoped saved choice.</summary>
public static partial class OllamaModelDownload
{
    public const string CatalogUrl = "https://ollama.com/search";
    public static Uri CatalogUri { get; } = new(CatalogUrl);
    public const string DownloadHint =
        "Enter a model name, such as deepseek-r1 or VicRodger27/Writex:4b, or paste its ollama run or ollama pull command. " +
        "Keep the publisher, name and tag together. A name without a tag asks for latest, which some publishers don't offer. " +
        "Downloading uses the internet and can take several GB of disk space.";
    public const string Canceled =
        "Download canceled. Ollama keeps the downloaded parts so you can resume by choosing Download again.";
    public const string Saved =
        "Model choice saved. Check the AI cleanup status above to see when it's ready. Other unsaved changes stay unsaved.";
    private const string MissingModel =
        "Ollama couldn't find that model and tag. Copy the exact name and tag from the catalog's run command; some publishers don't offer latest.";

    public static OllamaDownloadRequest Validate(string? input)
    {
        var model = input?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            return new(null, "Enter the name of the model you want to download.");
        }

        foreach (var command in new[] { "ollama run ", "ollama pull " })
        {
            if (model.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                model = model[command.Length..].Trim();
                break;
            }
        }

        if (model.Length > 200 || !ModelName().IsMatch(model))
        {
            return new(null, "Enter a model name or its ollama run command, without a web address, extra options or instructions.");
        }

        var colon = model.LastIndexOf(':');
        if (colon >= 0 && model[(colon + 1)..].EndsWith("cloud", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, "Choose a model that runs on this PC. Ollama cloud models aren't supported here.");
        }

        return new(model, null);
    }

    public static string Describe(OllamaDownloadProgress progress) =>
        progress.Stage switch
        {
            OllamaDownloadStage.Downloading when progress.TotalBytes > 0 =>
                $"Downloading a model file: {(progress.CompletedBytes == 0 ? "0 MB" : LocalAppSetup.FormatSize(progress.CompletedBytes))} of " +
                $"{LocalAppSetup.FormatSize(progress.TotalBytes)} ({progress.Percent.GetValueOrDefault():0}%).",
            OllamaDownloadStage.Verifying => "Checking the downloaded files...",
            OllamaDownloadStage.Installing => "Adding the model to Ollama...",
            OllamaDownloadStage.Completed => "Download complete.",
            _ => "Ollama is preparing the download...",
        };

    public static string Failure(Exception failure) =>
        failure switch
        {
            OperationCanceledException => "The download timed out. Choose Download to resume it.",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } or
                ResponseError { Message: "pull model manifest: file does not exist" } => MissingModel,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                "Ollama couldn't access that model. Check it in the catalog and sign in to Ollama if needed.",
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
                "Scribe can't reach Ollama. Open its Windows app, then choose Download again.",
            _ => "Ollama couldn't finish the download. Check that it's open and you have enough disk space, then try again.",
        };

    public static string SwitchQuestion(string model) =>
        $"Use {model} for AI cleanup? This saves the model choice immediately. Other unsaved changes stay unsaved.";

    public static string? CompletionProblem(LocalServerState? reading, string model)
    {
        if (reading is not { Reach: LocalServerReach.Reached })
        {
            return "Downloaded, but Scribe couldn't refresh Ollama's model list. Choose Check again.";
        }

        return reading.Models.Any(listed => LocalServerClient.SameModel(listed.Id, model))
            ? null
            : "Downloaded, but Ollama doesn't list it as a model for AI cleanup. Choose a text model from the catalog.";
    }

    /// <summary>Mutates only the fields that choose Ollama and its model; never saves an editing document.</summary>
    public static void ApplyChoice(AppSettings settings, string model)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var request = Validate(model);
        if (request.Model is not { } name)
        {
            throw new ArgumentException(request.Error, nameof(model));
        }

        var fields = CustomServiceFields.ForSave(
            LocalServerApp.Ollama, name, CustomServiceFields.OtherService(settings), settings);
        settings.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        settings.AiCleanupCustomEndpoint = fields.Stored.Endpoint;
        settings.AiCleanupCustomModel = fields.Stored.Model;
        settings.AiCleanupCustomApiKey = fields.Stored.ApiKey;
        settings.AiCleanupCustomApiStyle = fields.Stored.ApiStyle;
        settings.AiCleanupOtherServiceEndpoint = fields.Remembered.Endpoint;
        settings.AiCleanupOtherServiceModel = fields.Remembered.Model;
        settings.AiCleanupOtherServiceApiKey = fields.Remembered.ApiKey;
        settings.AiCleanupOtherServiceApiStyle = fields.Remembered.ApiStyle;
    }

    /// <summary>Keeps the window's choice in step without discarding any other staged settings.</summary>
    public static void CopyChoice(AppSettings stored, AppSettings draft)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(draft);
        draft.AiCleanupProvider = stored.AiCleanupProvider;
        draft.AiCleanupCustomEndpoint = stored.AiCleanupCustomEndpoint;
        draft.AiCleanupCustomModel = stored.AiCleanupCustomModel;
        draft.AiCleanupCustomApiKey = stored.AiCleanupCustomApiKey;
        draft.AiCleanupCustomApiStyle = stored.AiCleanupCustomApiStyle;
        draft.AiCleanupOtherServiceEndpoint = stored.AiCleanupOtherServiceEndpoint;
        draft.AiCleanupOtherServiceModel = stored.AiCleanupOtherServiceModel;
        draft.AiCleanupOtherServiceApiKey = stored.AiCleanupOtherServiceApiKey;
        draft.AiCleanupOtherServiceApiStyle = stored.AiCleanupOtherServiceApiStyle;
    }

    [GeneratedRegex(@"\A(?:[a-zA-Z0-9][a-zA-Z0-9._-]*/)?[a-zA-Z0-9][a-zA-Z0-9._-]*(?::[a-zA-Z0-9][a-zA-Z0-9._-]*)?\z")]
    private static partial Regex ModelName();
}
