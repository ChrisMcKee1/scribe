using Scribe.Core.Models;
using Scribe.Core.Transcription;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Settings;

public sealed record RecordingIndicatorState(bool ShowPositionPicker, bool ShowPreview);

public static class RecordingIndicatorRules
{
    public static RecordingIndicatorState Describe(bool showRecordingIndicator) =>
        new(showRecordingIndicator, showRecordingIndicator);
}

public static class ShortcutCaveats
{
    private const uint PageUp = 0x21;
    private const uint PageDown = 0x22;
    private const uint RightShift = 0xA1;
    private const uint NumLock = 0x90;
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;
    private const uint LeftShift = 0xA0;
    private const uint LeftWin = 0x5B;
    private const uint RightWin = 0x5C;

    public static string? For(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return null;
        }

        // Only a button bound on its own, with no modifier, is swallowed whenever it is pressed. In a chord the first input
        // still reaches the app, and with a modifier the button alone still does its usual job, so neither gets this line.
        if (binding.Modifiers == KeyModifiers.None && binding.SecondaryVirtualKey is null && MouseButtons.IsBindable(binding.VirtualKey))
        {
            return "While Scribe runs, this mouse button doesn't do its usual job in other apps, unless you hold Ctrl, Shift, Alt or the Windows key.";
        }

        if (binding.Modifiers == KeyModifiers.None && binding.SecondaryVirtualKey is null)
        {
            return binding.VirtualKey switch
            {
                PageDown => "While Scribe runs, Page Down on its own doesn't reach other apps, so it won't page through documents or change slides. With Ctrl, Shift, Alt or the Windows key it works as usual. On many laptops, Page Down is Fn plus the Down arrow.",
                PageUp => "While Scribe runs, Page Up on its own doesn't reach other apps, so it won't page through documents or change slides. With Ctrl, Shift, Alt or the Windows key it works as usual. On many laptops, Page Up is Fn plus the Up arrow.",
                RightShift => "Holding Right Shift can turn on Filter Keys in Windows. Try a two-key combination instead.",
                NumLock => "Holding Num Lock can turn on Toggle Keys in Windows. Try another key.",
                LeftControl or RightControl or LeftAlt or RightAlt or LeftShift or LeftWin or RightWin =>
                    "If you use Sticky Keys, holding this key can turn them on.",
                _ => null,
            };
        }

        return binding.Modifiers.HasFlag(KeyModifiers.Win) || binding.VirtualKey is LeftWin or RightWin
            ? "This combination replaces a Windows shortcut while Scribe runs."
            : null;
    }
}

public sealed record TranscriptionModelChoice(
    string Id,
    string Label,
    string Hint,
    bool IsSelected,
    bool IsInstalled,
    bool ShowInstall,
    string StatusText);

public sealed record MaiTranscribeStyleChoice(MaiTranscribeStyle Style, string Label);

public sealed record SpeechModelSetupStatus(
    string CurrentModelLine,
    string GuidanceLine,
    string AuthStatusLine,
    bool IsMaiSelected,
    bool IsReady);

public static class TranscriptionModelChoices
{
    public static Uri SetupGuideUri { get; } =
        new("https://github.com/ChrisMcKee1/scribe/blob/main/docs/mai-transcribe-setup.md");

    public const string MaiTranscribeDisclosure =
        "When MAI-Transcribe-2 is selected, Scribe sends each dictation's recorded audio and up to 500 words from your " +
        "dictionary and the word packs you let AI cleanup use to your Microsoft Foundry connection. If Microsoft Foundry " +
        "isn't set up or can't be reached, Scribe uses Parakeet on this PC.";

    public const string MaiTranscribeModelNameNote =
        "Model name: Scribe sends MAI-Transcribe-2 automatically in enhanced mode, so you don't need to enter a separate speech deployment name.";

    public static SpeechModelSetupStatus DescribeSetupStatus(
        string? selectedModelId,
        string? maiEndpointOverride,
        string? foundryEndpoint,
        AzureAuthMode authMode,
        string? apiKey,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        bool azureCliSignedIn = true)
    {
        var model = TranscriptionModelCatalog.Resolve(selectedModelId);
        if (!model.IsCloud)
        {
            return new SpeechModelSetupStatus(
                CurrentModelLine: $"Current speech model: {LabelFor(model)} (runs on this PC).",
                GuidanceLine:
                    "Want to use another speech model (such as Moonshine or MAI-Transcribe-2)? Choose it on the Advanced page. " +
                    "To use MAI-Transcribe-2, also set up your Microsoft Foundry sign-in (Azure CLI, app registration or API key) " +
                    "and resource address on the AI cleanup page.",
                AuthStatusLine: string.Empty,
                IsMaiSelected: false,
                IsReady: true);
        }

        var hasEndpoint = MaiTranscribeEndpointResolver.TryResolve(
            maiEndpointOverride,
            foundryEndpoint,
            out _,
            out _);
        var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        var spComplete = AzureServicePrincipalValidator.IsComplete(tenantId, clientId, clientSecret);

        string authStatus;
        bool authReady;
        if (!hasEndpoint)
        {
            authReady = false;
            authStatus =
                "Setup needed: Open the AI cleanup page, choose Microsoft Foundry, and sign in or enter your Microsoft Foundry " +
                "resource address (or enter a Speech resource address below).";
        }
        else if (hasApiKey)
        {
            authReady = true;
            authStatus =
                "Microsoft Foundry setup: Ready (using your saved API key and resource address; model name MAI-Transcribe-2 is sent automatically).";
        }
        else if (authMode == AzureAuthMode.ServicePrincipal)
        {
            authReady = spComplete;
            authStatus = spComplete
                ? "Microsoft Foundry setup: Ready (using your app registration and resource address; model name MAI-Transcribe-2 is sent automatically)."
                : "Setup needed on AI cleanup: Enter your Directory (tenant) ID, Application (client) ID and Client secret under Microsoft Foundry.";
        }
        else
        {
            authReady = azureCliSignedIn;
            authStatus = azureCliSignedIn
                ? "Microsoft Foundry setup: Ready (using your Azure CLI sign-in and resource address; model name MAI-Transcribe-2 is sent automatically)."
                : "Setup needed on AI cleanup: Sign in with Azure CLI under Microsoft Foundry, or switch to an app registration or API key.";
        }

        return new SpeechModelSetupStatus(
            CurrentModelLine: "Current speech model: MAI-Transcribe-2, 60 languages (Microsoft Foundry, preview).",
            GuidanceLine:
                "MAI-Transcribe-2 uses your Microsoft Foundry sign-in (Azure CLI, app registration or API key) and resource address " +
                "from the AI cleanup page, and sends model name MAI-Transcribe-2 automatically.",
            AuthStatusLine: authStatus,
            IsMaiSelected: true,
            IsReady: authReady);
    }

    public static IReadOnlyList<MaiTranscribeStyleChoice> StyleChoices { get; } =
    [
        new(MaiTranscribeStyle.Clean, "Clean (remove filler words)"),
        new(MaiTranscribeStyle.Verbatim, "Verbatim (as spoken)"),
    ];

    public static IReadOnlyList<TranscriptionModelChoice> Build(
        string? selectedModelId,
        IReadOnlySet<string> installedModelIds,
        bool includeMaiTranscribe = false)
    {
        ArgumentNullException.ThrowIfNull(installedModelIds);

        var resolved = TranscriptionModelCatalog.Resolve(selectedModelId);
        var selected = resolved.IsCloud && !includeMaiTranscribe
            ? TranscriptionModelCatalog.DefaultId
            : resolved.Id;
        var choices = TranscriptionModelCatalog.Curated
            .Select(model =>
            {
                var installed = installedModelIds.Contains(model.Id);
                return new TranscriptionModelChoice(
                    model.Id,
                    LabelFor(model),
                    $"{HintFor(model)} {FormatSize(model.DownloadSize)}. {(installed ? "Downloaded." : "Not downloaded.")}",
                    model.Id == selected,
                    installed,
                    ShowInstall: !installed,
                    installed ? "Downloaded" : string.Empty);
            })
            .ToList();

        if (includeMaiTranscribe)
        {
            var mai = TranscriptionModelCatalog.MaiTranscribe2;
            choices.Add(new TranscriptionModelChoice(
                mai.Id,
                LabelFor(mai),
                HintFor(mai),
                mai.Id == selected,
                IsInstalled: true,
                ShowInstall: false,
                StatusText: "Cloud"));
        }

        return choices;
    }

    private static string LabelFor(TranscriptionModel model) => model.Id switch
    {
        TranscriptionModelCatalog.DefaultId => "Parakeet, 25 languages (recommended)",
        "moonshine-base-en-int8" => "Moonshine Base, English only",
        "moonshine-tiny-en-int8" => "Moonshine Tiny, English only",
        TranscriptionModelCatalog.MaiTranscribe2Id => "MAI-Transcribe-2, 60 languages (Microsoft Foundry, preview)",
        _ => model.DisplayName,
    };

    private static string HintFor(TranscriptionModel model) => model.Id switch
    {
        TranscriptionModelCatalog.DefaultId => "Understands about 25 European languages.",
        "moonshine-base-en-int8" => "Smaller and fast. English only.",
        "moonshine-tiny-en-int8" => "Smallest and fastest. English only.",
        TranscriptionModelCatalog.MaiTranscribe2Id =>
            "Uses your Microsoft Foundry connection to recognize 60 languages with your vocabulary. Uses Parakeet on this PC if unavailable.",
        _ => model.Description,
    };

    private static string FormatSize(long bytes)
    {
        var mb = (int)Math.Round(bytes / 1_000_000d);
        return mb >= 1000
            ? $"about {mb / 1000d:0.#} GB"
            : $"{mb} MB";
    }
}
