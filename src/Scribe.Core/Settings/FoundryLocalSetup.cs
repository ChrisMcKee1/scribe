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
    ModelFailed,
}

public sealed record FoundryLocalSetupDescription(
    AiCleanupStatusKind Kind,
    string Text,
    string? ActionText,
    bool CanUnload,
    FoundryLocalSetupStage Stage = FoundryLocalSetupStage.NotSetUp);

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
        FoundryLocalSetupDescription description = stage switch
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
            FoundryLocalSetupStage.ModelFailed => new(
                AiCleanupStatusKind.Error,
                $"Couldn't download or load {modelName}. Try again, or choose another model.",
                "Try again",
                false),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };
        return description with { Stage = stage };
    }

    public static FoundryLocalSetupStage FromCleanupStatus(CleanupStatus status, bool runtimeReady, bool modelCached) =>
        FromCleanupStatus(status, runtimeReady, modelCached, modelLoaded: null);

    public static FoundryLocalSetupStage FromCleanupStatus(CleanupStatus status, bool runtimeReady, bool modelCached, bool? modelLoaded)
    {
        // Work in progress outranks what is settled: the service reports Downloading while it loads a cached model
        // (TextCleanupService publishes it before LoadAsync), so a cached model must not offer another Load meanwhile.
        switch (status)
        {
            case CleanupStatus.Initializing:
                return FoundryLocalSetupStage.SettingUp;
            case CleanupStatus.Downloading:
                return FoundryLocalSetupStage.DownloadingOrLoading;
        }

        // Known residency decides next. A manual unload publishes Unavailable (DecideResidentChange), which is not a
        // failure: the model is on disk and one Load away.
        if (modelLoaded == true)
        {
            return FoundryLocalSetupStage.Loaded;
        }

        if (modelLoaded == false)
        {
            if (modelCached)
            {
                return FoundryLocalSetupStage.CachedUnloaded;
            }

            return status == CleanupStatus.Unavailable ? Failure(runtimeReady)
                : runtimeReady ? FoundryLocalSetupStage.RuntimeReady
                : FoundryLocalSetupStage.NotSetUp;
        }

        if (status == CleanupStatus.Ready)
        {
            return FoundryLocalSetupStage.Loaded;
        }

        if (modelCached)
        {
            return status == CleanupStatus.Unavailable && !runtimeReady
                ? FoundryLocalSetupStage.Failed
                : FoundryLocalSetupStage.Checking;
        }

        return status == CleanupStatus.Unavailable ? Failure(runtimeReady)
            : runtimeReady ? FoundryLocalSetupStage.RuntimeReady
            : FoundryLocalSetupStage.NotSetUp;
    }

    // Unavailable after the runtime came up means the model could not be found, downloaded or loaded; only a runtime
    // that never came up is the runtime failure.
    private static FoundryLocalSetupStage Failure(bool runtimeReady) =>
        runtimeReady ? FoundryLocalSetupStage.ModelFailed : FoundryLocalSetupStage.Failed;
}
