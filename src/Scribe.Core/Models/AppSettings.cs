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

    /// <summary>Where the recording overlay appears on screen.</summary>
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomCenter;

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

    /// <summary>
    /// In toggle mode, end the dictation automatically after a few seconds of silence instead of
    /// waiting for the second key press. Off by default (noisy rooms can misfire the detector).
    /// </summary>
    public bool AutoStopOnSilence { get; set; }

    /// <summary>Apply the user dictionary and casing/spacing fixups to decoded text.</summary>
    public bool ApplyPostProcessing { get; set; } = true;

    /// <summary>
    /// Ids of the dictionary libraries the user has switched on. Each enabled library's entries are
    /// layered on top of the base dictionary (the user's own entries win on conflict) and feed both
    /// the deterministic post-processor and the AI cleanup glossary. Empty by default, so libraries
    /// are strictly opt-in and never change behaviour for someone who hasn't chosen any. A plain
    /// string list, deep-copied in <see cref="Clone"/>.
    /// </summary>
    public List<string> EnabledDictionaryLibraryIds { get; set; } = new();

    /// <summary>
    /// Run transcribed text through an AI model to fix punctuation, capitalization and grammar
    /// before injection. Off by default. Depending on <see cref="AiCleanupProvider"/> this uses an
    /// on-device Foundry Local model (downloaded on first use) or a model deployed in the user's
    /// Microsoft Foundry account. Always degrades to raw text when unavailable.
    /// </summary>
    public bool EnableAiCleanup { get; set; }

    /// <summary>Which engine performs AI cleanup (on-device Foundry Local or Microsoft Foundry).</summary>
    public Cleanup.CleanupProvider AiCleanupProvider { get; set; } = Cleanup.CleanupProvider.FoundryLocal;

    /// <summary>Foundry Local model alias used for AI cleanup when the provider is Foundry Local.</summary>
    public string AiCleanupModel { get; set; } = Cleanup.CleanupModelCatalog.DefaultAlias;

    /// <summary>
    /// Azure OpenAI / Microsoft Foundry resource endpoint used when the provider is Microsoft Foundry,
    /// e.g. <c>https://my-resource.openai.azure.com/</c>. Discovered from the user's Azure sign-in.
    /// </summary>
    public string? AiCleanupAzureEndpoint { get; set; }

    /// <summary>Name of the Azure model deployment to call when the provider is Microsoft Foundry.</summary>
    public string? AiCleanupAzureDeployment { get; set; }

    /// <summary>
    /// User-editable writing-style guidance appended to the AI cleanup prompt. Describes the tone,
    /// punctuation and structure the model should apply when polishing a transcript. Blank means use
    /// <see cref="Cleanup.CleanupPrompt.DefaultWritingStyle"/>, so improvements to the default flow
    /// through to users who never customized it.
    /// </summary>
    public string AiCleanupWritingStyle { get; set; } = string.Empty;

    /// <summary>
    /// Which cleanup prompt preamble to use. <see cref="Cleanup.CleanupPromptStyle.Auto"/> (default)
    /// picks by provider — the terse local-optimized prompt for on-device Foundry Local, the frontier
    /// prompt for cloud/bring-your-own — while letting the user force either. Hot-swappable: changing it
    /// re-applies on the next dictation with no restart, like the other cleanup settings.
    /// </summary>
    public Cleanup.CleanupPromptStyle AiCleanupPromptStyle { get; set; } = Cleanup.CleanupPromptStyle.Auto;

    /// <summary>
    /// User override for the frontier-model cleanup prompt (the guardrail preamble that precedes the
    /// writing style). Blank uses <see cref="Cleanup.CleanupPrompt.DefaultFrontierPrompt"/>, so
    /// improvements to the built-in default flow through to users who never customized it. Restorable
    /// on its own from settings.
    /// </summary>
    public string AiCleanupFrontierPrompt { get; set; } = string.Empty;

    /// <summary>
    /// User override for the local-model cleanup prompt (the guardrail preamble that precedes the
    /// writing style). Blank uses <see cref="Cleanup.CleanupPrompt.DefaultLocalPrompt"/>. Restorable on
    /// its own from settings, independently of the frontier prompt.
    /// </summary>
    public string AiCleanupLocalPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Optional Azure AD (Entra) tenant id (GUID) used when the provider is Microsoft Foundry and
    /// authentication falls back to the user's sign-in. <see cref="DefaultAzureCredential"/> otherwise
    /// uses whichever tenant the Azure CLI is currently set to, which is wrong for users who juggle a
    /// corporate and a demo tenant. Leave blank to use the active <c>az login</c> tenant. Ignored when
    /// an API key is supplied.
    /// </summary>
    public string? AiCleanupAzureTenantId { get; set; }

    /// <summary>
    /// Optional Azure OpenAI API key. When set, the Azure provider authenticates with this key instead
    /// of the user's <c>az login</c> (DefaultAzureCredential). Encrypted at rest with Windows DPAPI via
    /// <see cref="DpapiProtectedStringConverter"/>; this property exposes the plaintext in memory.
    /// </summary>
    [JsonConverter(typeof(DpapiProtectedStringConverter))]
    public string? AiCleanupAzureApiKey { get; set; }

    /// <summary>
    /// Base URL of a bring-your-own OpenAI-compatible endpoint (Ollama, LM Studio, vLLM,
    /// OpenRouter, api.openai.com), e.g. <c>http://localhost:11434/v1</c>. Used when
    /// <see cref="AiCleanupProvider"/> is <see cref="Cleanup.CleanupProvider.OpenAiCompatible"/>.
    /// </summary>
    public string? AiCleanupCustomEndpoint { get; set; }

    /// <summary>Model name to request from the custom endpoint (e.g. <c>qwen3:4b</c>).</summary>
    public string? AiCleanupCustomModel { get; set; }

    /// <summary>
    /// Optional API key for the custom endpoint (local servers don't need one). DPAPI-encrypted at
    /// rest, same as the Azure key.
    /// </summary>
    [JsonConverter(typeof(DpapiProtectedStringConverter))]
    public string? AiCleanupCustomApiKey { get; set; }

    /// <summary>
    /// How decoded text is placed into the focused app. Unicode typing is the default because it
    /// works in the widest range of apps (including paste-blocking fields) and never touches the
    /// clipboard.
    /// </summary>
    public InjectionMethod InjectionMethod { get; set; } = InjectionMethod.UnicodeType;

    /// <summary>
    /// What happens to line breaks before injection. Defaults to flattening them to spaces only
    /// when the focused app is a known terminal, where an injected newline acts as Enter and
    /// would submit a partial message.
    /// </summary>
    public NewlineInjectionMode NewlineHandling { get; set; } = NewlineInjectionMode.SmartFlatten;

    /// <summary>Persist a copy of each capture's audio alongside its history entry.</summary>
    public bool StoreAudioHistory { get; set; }

    /// <summary>
    /// Per-app dictation profiles, evaluated in order against the focused app's process name.
    /// The first match overrides the writing style and/or line-break handling for that dictation.
    /// </summary>
    public List<AppProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Set once the first-run welcome has been shown, so it never reappears. Scribe is tray-only
    /// with no main window, so this gate is what stops a returning user seeing the intro again.
    /// A plain value type, so the memberwise <see cref="Clone"/> copies it correctly.
    /// </summary>
    public bool HasCompletedFirstRun { get; set; }

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone()
    {
        // Deep-copy the profile list: MemberwiseClone would share it, so an edit in the settings
        // editor could mutate the snapshot the dictation loop is reading.
        var clone = (AppSettings)MemberwiseClone();
        clone.Profiles = Profiles.Select(p => new AppProfile
        {
            Name = p.Name,
            ProcessNames = new List<string>(p.ProcessNames),
            WritingStyle = p.WritingStyle,
            NewlineHandling = p.NewlineHandling,
        }).ToList();
        // Same reason as Profiles: the id list is mutable, so give the clone its own copy.
        clone.EnabledDictionaryLibraryIds = new List<string>(EnabledDictionaryLibraryIds);
        return clone;
    }
}
