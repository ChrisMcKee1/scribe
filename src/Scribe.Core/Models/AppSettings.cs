namespace Scribe.Core.Models;

using System.Text.Json.Serialization;
using Scribe.Core.Security;

/// <summary>
/// User-configurable application settings. Mutable POCO so it can back a settings view-model
/// and be (de)serialized to the settings store. Construct via <see cref="CreateDefault"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>WASAPI capture device id; <see langword="null"/> uses the system default.</summary>
    public string? InputDeviceId { get; set; }

    /// <summary>Friendly name of the selected device (for display only).</summary>
    public string? InputDeviceName { get; set; }

    public HotkeyBinding Hotkey { get; set; } = HotkeyBinding.Default;

    /// <summary>Show the always-on-top recording overlay while capturing.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Register the app to start at user logon.</summary>
    public bool LaunchOnLogin { get; set; }

    /// <summary>Decode thread count for sherpa-onnx; 0 lets the app pick a sensible default.</summary>
    public int DecodeThreads { get; set; }

    /// <summary>
    /// Use beam-search decoding (<c>modified_beam_search</c>) instead of greedy. Slightly more
    /// accurate on hard audio at a latency cost. Takes effect after restarting Scribe because the
    /// recognizer is warm-loaded once.
    /// </summary>
    public bool UseHighAccuracyDecoding { get; set; }

    /// <summary>Trim leading/trailing silence and reject no-speech captures using VAD.</summary>
    public bool UseVoiceActivityDetection { get; set; } = true;

    /// <summary>Apply the user dictionary and casing/spacing fixups to decoded text.</summary>
    public bool ApplyPostProcessing { get; set; } = true;

    /// <summary>
    /// Run transcribed text through an AI model to fix punctuation, capitalization and grammar
    /// before injection. Off by default. Depending on <see cref="AiCleanupProvider"/> this uses an
    /// on-device Foundry Local model (downloaded on first use) or a model deployed in the user's
    /// Azure AI Foundry account. Always degrades to raw text when unavailable.
    /// </summary>
    public bool EnableAiCleanup { get; set; }

    /// <summary>Which engine performs AI cleanup (on-device Foundry Local or Azure AI Foundry).</summary>
    public Cleanup.CleanupProvider AiCleanupProvider { get; set; } = Cleanup.CleanupProvider.FoundryLocal;

    /// <summary>Foundry Local model alias used for AI cleanup when the provider is Foundry Local.</summary>
    public string AiCleanupModel { get; set; } = Cleanup.CleanupModelCatalog.DefaultAlias;

    /// <summary>
    /// Azure OpenAI / AI Foundry resource endpoint used when the provider is Azure AI Foundry,
    /// e.g. <c>https://my-resource.openai.azure.com/</c>. Discovered from the user's Azure sign-in.
    /// </summary>
    public string? AiCleanupAzureEndpoint { get; set; }

    /// <summary>Name of the Azure model deployment to call when the provider is Azure AI Foundry.</summary>
    public string? AiCleanupAzureDeployment { get; set; }

    /// <summary>
    /// Optional Azure OpenAI API key. When set, the Azure provider authenticates with this key instead
    /// of the user's <c>az login</c> (DefaultAzureCredential). Encrypted at rest with Windows DPAPI via
    /// <see cref="DpapiProtectedStringConverter"/>; this property exposes the plaintext in memory.
    /// </summary>
    [JsonConverter(typeof(DpapiProtectedStringConverter))]
    public string? AiCleanupAzureApiKey { get; set; }

    /// <summary>
    /// How decoded text is placed into the focused app. Unicode typing is the default because it
    /// works in the widest range of apps (including paste-blocking fields) and never touches the
    /// clipboard.
    /// </summary>
    public InjectionMethod InjectionMethod { get; set; } = InjectionMethod.UnicodeType;

    /// <summary>Persist a copy of each capture's audio alongside its history entry.</summary>
    public bool StoreAudioHistory { get; set; }

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
