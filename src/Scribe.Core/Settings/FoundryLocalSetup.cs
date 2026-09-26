using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

public enum FoundryLocalSetupStage
{
    NotSetUp,
    SettingUp,
    RuntimeReady,
    CachedUnloaded,
    Checking,
    DownloadingOrLoading,
    Loaded,
    Failed,
}

public sealed record FoundryLocalSetupDescription(
    AiCleanupStatusKind Kind,
    string Text,
    string? ActionText,
    bool CanUnload);

public static class FoundryLocalSetup
{
    public static FoundryLocalSetupDescription Describe(
        FoundryLocalSetupStage stage,
        string modelName,
        string? modelSize = null,
        string? progressText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var size = string.IsNullOrWhiteSpace(modelSize) ? "the model" : modelSize;
        return stage switch
        {
            FoundryLocalSetupStage.NotSetUp => new(
                AiCleanupStatusKind.Warning,
                "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.",
                "Set up",
                CanUnload: false),
            FoundryLocalSetupStage.SettingUp => new(AiCleanupStatusKind.Busy, "Setting up. The first time can take a while.", null, false),
            FoundryLocalSetupStage.RuntimeReady => new(AiCleanupStatusKind.Info, $"Ready to download {modelName} ({size}).", "Download and load", false),
            FoundryLocalSetupStage.CachedUnloaded => new(AiCleanupStatusKind.Info, "No model is loaded.", "Load", false),
            FoundryLocalSetupStage.Checking => new(AiCleanupStatusKind.Info, "Checking...", "Unload", false),
            FoundryLocalSetupStage.DownloadingOrLoading => new(AiCleanupStatusKind.Busy, progressText ?? $"Loading {modelName}...", null, false),
            FoundryLocalSetupStage.Loaded => new(AiCleanupStatusKind.Success, $"{modelName} is ready.", "Unload", true),
            FoundryLocalSetupStage.Failed => new(
                AiCleanupStatusKind.Error,
                "Couldn't start the on-device AI runtime. Try again, or choose another AI service.",
                "Try again",
                false),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };
    }

    public static FoundryLocalSetupStage FromCleanupStatus(CleanupStatus status, bool runtimeReady, bool modelCached) =>
        FromCleanupStatus(status, runtimeReady, modelCached, modelLoaded: null);

    public static FoundryLocalSetupStage FromCleanupStatus(CleanupStatus status, bool runtimeReady, bool modelCached, bool? modelLoaded)
    {
        if (modelLoaded == true)
        {
            return FoundryLocalSetupStage.Loaded;
        }

        if (modelLoaded == false && modelCached)
        {
            return FoundryLocalSetupStage.CachedUnloaded;
        }

        if (modelLoaded is null && modelCached)
        {
            return FoundryLocalSetupStage.Checking;
        }

        return status switch
        {
            CleanupStatus.Ready => FoundryLocalSetupStage.Loaded,
            CleanupStatus.Initializing => FoundryLocalSetupStage.SettingUp,
            CleanupStatus.Downloading => FoundryLocalSetupStage.DownloadingOrLoading,
            CleanupStatus.Unavailable => FoundryLocalSetupStage.Failed,
            _ => runtimeReady ? FoundryLocalSetupStage.RuntimeReady : FoundryLocalSetupStage.NotSetUp,
        };
    }
}
