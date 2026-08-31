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

    /// <summary>
    /// Optional second trigger that always bypasses AI cleanup. Null keeps the legacy single-hotkey
    /// behavior and lets existing settings continue unchanged.
    /// </summary>
    public HotkeyBinding? DictationOnlyHotkey { get; set; }

    /// <summary>
    /// Enables the text action palette: select text in any app, press
    /// <see cref="TextActionsHotkey"/>, and pick a transformation to apply to it. Off by default,
    /// because it reads the selection out of whatever app is in front and that must be something the
    /// user switched on deliberately.
    /// </summary>
    public bool EnableTextActions { get; set; }

    /// <summary>
    /// Trigger for the text action palette. Null means unbound, so the palette is reachable only from
    /// the tray menu. Carried on the existing keyboard hook as a third trigger rather than a second
    /// hook: two low-level hooks in one process means two callbacks per keystroke inside one
    /// LowLevelHooksTimeout budget, and two reconcilers competing over the same physical keys.
    /// </summary>
    public HotkeyBinding? TextActionsHotkey { get; set; }

    /// <summary>
    /// Show the result before it replaces the selection. On by default and deliberately so: replacing
    /// text the user already has is destructive in a way dictation is not, and Ctrl+Z in the target
    /// app cannot reliably undo a multi-chunk injection.
    /// </summary>
    public bool PreviewTextActions { get; set; } = true;

    /// <summary>
    /// Show the floating dock: a small always-on-top tile that opens the palette for the current
    /// selection. Unlike the tray menu, clicking it does not take focus, so the selection survives.
    /// </summary>
    public bool ShowTextActionDock { get; set; } = true;

    /// <summary>Saved dock position in WPF logical units. Null parks it above the tray.</summary>
    public double? TextActionDockLeft { get; set; }

    /// <summary>Saved dock position in WPF logical units. Null parks it above the tray.</summary>
    public double? TextActionDockTop { get; set; }

    /// <summary>Show the always-on-top recording overlay while capturing.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Where the recording overlay appears on screen.</summary>
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomCenter;

    /// <summary>Register the app to start at user logon.</summary>
    public bool LaunchOnLogin { get; set; }

    /// <summary>Decode thread count for sherpa-onnx; 0 lets the app pick a sensible default.</summary>
    public int DecodeThreads { get; set; }

    /// <summary>Id of the offline speech-recognition model to load after the next restart.</summary>
    public string TranscriptionModelId { get; set; } = Transcription.TranscriptionModelCatalog.DefaultId;

    /// <summary>Trim leading/trailing silence and reject no-speech captures using VAD.</summary>
    public bool UseVoiceActivityDetection { get; set; } = true;

    /// <summary>
    /// Minutes of idle (no dictation, not recording) after which the speech models are unloaded to
    /// return their memory to the OS; the next dictation reloads them with a one-to-two-second
    /// warm-up. 0 keeps the models resident forever (the pre-0.3.16 behavior). Ten minutes keeps
    /// Scribe out of the "top memory" list while it sits in the tray, which is most of its life,
    /// without touching back-to-back dictation sessions.
    /// </summary>
    public int ReleaseModelsAfterIdleMinutes { get; set; } = 10;

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
    /// the deterministic post-processor and the AI cleanup glossary. A plain string list,
    /// deep-copied in <see cref="Clone"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to empty here on purpose. <see cref="CreateDefault"/> seeds
    /// <see cref="DefaultLibraryIds"/> instead, so a fresh install gets them while an existing
    /// install that predates a library is never silently opted in by deserialization filling in the
    /// property initializer for a key its JSON does not contain.
    /// </remarks>
    public List<string> EnabledDictionaryLibraryIds { get; set; } = [];

    /// <summary>
    /// Libraries switched on for a fresh install. Only the AI vocabulary is on by default: model
    /// names and AI terminology are the terms Parakeet gets wrong most often and that no prompt can
    /// recover reliably, and unlike the platform-specific packs they are useful to nearly everyone
    /// who dictates about software. Everything else stays opt-in.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultLibraryIds =
    [
        "ai-model-names",
        "ai-terminology",
    ];

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
    /// Azure subscription id (GUID) that model discovery is filtered to in Settings. Null lists
    /// deployments from every subscription the sign-in can see, which surprises users whose
    /// account spans shared or foreign projects. It also pins token authentication to the identity
    /// that owns a selected deployment when browsing across subscriptions.
    /// </summary>
    public string? AiCleanupAzureSubscriptionId { get; set; }

    /// <summary>Friendly name of the filtered subscription (for display only).</summary>
    public string? AiCleanupAzureSubscriptionName { get; set; }

    /// <summary>Tenant that owns the selected Azure subscription (populated by CLI discovery).</summary>
    public string? AiCleanupAzureSubscriptionTenantId { get; set; }

    /// <summary>
    /// User-editable writing-style guidance appended to the AI cleanup prompt. Describes the tone,
    /// punctuation and structure the model should apply when polishing a transcript. Blank means use
    /// <see cref="Cleanup.CleanupPrompt.DefaultWritingStyle"/>, so improvements to the default flow
    /// through to users who never customized it.
    /// </summary>
    public string AiCleanupWritingStyle { get; set; } = string.Empty;

    /// <summary>
    /// Which cleanup prompt preamble to use. <see cref="Cleanup.CleanupPromptStyle.Auto"/> (default)
    /// picks by provider, the terse local-optimized prompt for on-device Foundry Local and the
    /// frontier prompt for cloud/bring-your-own, while letting the user force either. Hot-swappable: changing it
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
    /// authentication uses the user's Azure CLI sign-in. Leave blank to use the tenant attached to
    /// the selected subscription, or the CLI's active tenant when no subscription is selected.
    /// Ignored when an API key is supplied.
    /// </summary>
    public string? AiCleanupAzureTenantId { get; set; }

    /// <summary>
    /// Which Entra identity the Microsoft Foundry provider authenticates with. Defaults to the
    /// user's Azure CLI sign-in; <see cref="Settings.AzureAuthMode.ServicePrincipal"/> pins one
    /// app registration instead, which is what users who belong to several tenants need.
    /// Ignored when an API key is supplied.
    /// </summary>
    public Settings.AzureAuthMode AiCleanupAzureAuthMode { get; set; } = Settings.AzureAuthMode.AzureCli;

    /// <summary>
    /// Application (client) id of the Entra app registration used when
    /// <see cref="AiCleanupAzureAuthMode"/> is <see cref="Settings.AzureAuthMode.ServicePrincipal"/>.
    /// </summary>
    public string? AiCleanupAzureClientId { get; set; }

    /// <summary>
    /// Client secret for <see cref="AiCleanupAzureClientId"/>. Encrypted at rest with Windows DPAPI
    /// (current user) via <see cref="DpapiProtectedStringConverter"/>, the same treatment as the
    /// API keys; this property exposes the plaintext in memory. Never written to an environment
    /// variable or a script on disk.
    /// </summary>
    [JsonConverter(typeof(DpapiProtectedStringConverter))]
    public string? AiCleanupAzureClientSecret { get; set; }

    /// <summary>
    /// Optional Azure OpenAI API key. When set, the Azure provider authenticates with this key instead
    /// of the user's <c>az login</c>. Encrypted at rest with Windows DPAPI via
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

    /// <summary>
    /// Send a typed line break as Shift+Enter rather than a bare Enter. Chat apps (Teams, Slack,
    /// Discord) bind Enter to "send", so a cleaned multi-paragraph dictation submitted itself on the
    /// first paragraph break and typed the rest into an empty composer. Shift+Enter is the
    /// soft-newline chord in those apps and is indistinguishable from Enter in a plain text box, so
    /// this defaults to on; turn it off for an app that binds Shift+Enter to something else.
    /// </summary>
    public bool ShiftEnterLineBreaks { get; set; } = true;

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

    /// <summary>
    /// Set once the retired seed vocabulary has been disabled, so the cleanup runs at most once per
    /// install. Without the gate, re-enabling one of those entries on purpose would be undone on the
    /// next launch, which would read as Scribe fighting the user.
    /// </summary>
    public bool HasRetiredSeedVocabulary { get; set; }

    /// <summary>
    /// Set once the saved Foundry Local GPU demotions have been cleared. Those markers were written
    /// for any load failure, including a variant needing an absent execution provider, which Scribe
    /// now avoids outright. The gate keeps the clear to one launch so a genuinely broken GPU is not
    /// re-probed every time.
    /// </summary>
    public bool HasResetFoundryDemotions { get; set; }

    /// <summary>
    /// A settings object for a brand new install. Distinct from <c>new AppSettings()</c>: this is
    /// where first-run opt-ins live, so deserializing an existing install can never acquire them.
    /// </summary>
    public static AppSettings CreateDefault() => new()
    {
        EnabledDictionaryLibraryIds = [.. DefaultLibraryIds],
    };

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
