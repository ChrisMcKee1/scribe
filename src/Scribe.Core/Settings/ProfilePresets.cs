using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Ready-made per-app profiles offered in the settings window.
///
/// These exist because the profile editor previously started from a blank "New profile" row, which
/// gave no hint that the process names are Win32 image names without the .exe suffix, and no hint
/// that line-break handling is the setting that stops a dictation being submitted as several
/// messages. Production history showed dictations carrying paragraph breaks into GitHub Copilot,
/// Claude, ChatGPT, Microsoft Scout, Teams and VS Code, all of which act on Enter in their message
/// box, and none of which the built-in terminal list covers.
///
/// They are deliberately templates rather than defaults. Adding one is an explicit user action, so
/// an upgrade never silently changes how anyone's dictation is formatted. That also keeps the
/// process-name guesswork honest: an app the preset names wrongly simply does not match, and the
/// user can correct the list in place instead of fighting a hardcoded rule.
/// </summary>
public static class ProfilePresets
{
    /// <summary>
    /// A template: the profile itself plus a one-line explanation for the menu, and any name the preset had in an earlier
    /// release, which a profile added then still carries.
    /// </summary>
    public readonly record struct Preset(string Description, AppProfile Profile, IReadOnlyList<string>? FormerNames = null)
    {
        /// <summary>
        /// Whether a profile named <paramref name="name"/> is this preset already added. The menu greys out a preset by
        /// this name match, so a renamed preset keeps matching the profiles added under its former name.
        /// </summary>
        public bool IsNamed(string? name) =>
            name is not null &&
            (string.Equals(name, Profile.Name, StringComparison.OrdinalIgnoreCase) ||
             (FormerNames ?? []).Any(former => string.Equals(name, former, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Command windows (terminals and shells). Enter submits the command line, so a dictation the AI cleanup split
    /// into paragraphs would run several partial commands. The plain-text writing style is safe
    /// here because a shell has no use for markdown, bullets or code fences.
    /// </summary>
    public static Preset TerminalsAndShells => new(
        "Windows Terminal, PowerShell, Command Prompt and other command windows. Keeps a dictation on one line so it isn't run early, and asks AI cleanup for plain text.",
        new AppProfile
        {
            Name = "Command windows",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
            WritingStyle = "Plain text only. No markdown, no bullet points, no headings and no code fences.",
            ProcessNames =
            [
                "WindowsTerminal", "wt", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
                "alacritty", "wezterm-gui", "ConEmu64", "mintty", "Hyper", "Tabby", "warp",
                "kitty", "putty",
            ],
        },
        FormerNames: ["Terminals and shells"]);

    /// <summary>
    /// Editors and IDEs, kept separate from terminals and deliberately blunt about the trade-off.
    /// A process name cannot tell an editor pane from an integrated terminal, so this profile
    /// applies to BOTH, and it exists only for people who dictate into the integrated terminal
    /// often enough to accept losing line breaks in the editor. No writing style is attached: an
    /// editor is exactly where markdown and code fences are wanted.
    /// </summary>
    public static Preset IdeIntegratedTerminals => new(
        "For dictating into the command window inside a code editor, such as Visual Studio Code. Warning: this also removes line breaks while you edit code in that editor.",
        new AppProfile
        {
            Name = "Command windows in code editors",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
            ProcessNames =
            [
                "Code", "Code - Insiders", "VSCodium", "cursor", "windsurf", "devenv",
                "rider64", "idea64", "pycharm64", "goland64", "clion64", "webstorm64",
                "sublime_text", "notepad++", "zed",
            ],
        },
        FormerNames: ["IDE integrated terminals"]);

    /// <summary>
    /// Desktop AI assistants and chat clients whose composer sends on Enter. This is the preset
    /// that addresses the reported failure: AI cleanup introduces paragraph breaks that raw
    /// recognition never contains, and each one submitted a partial message.
    /// </summary>
    public static Preset AiChatAndAgents => new(
        "Claude, ChatGPT, GitHub Copilot, Microsoft 365 Copilot and Scout. Keeps a multi-paragraph dictation in one message.",
        new AppProfile
        {
            Name = "AI chat and agents",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
            ProcessNames =
            [
                "claude", "ChatGPT", "github", "GitHubCopilot", "M365Copilot",
                "Microsoft Scout", "scout", "Discord", "slack", "Perplexity",
            ],
        });

    /// <summary>
    /// Teams is kept separate on purpose. It is by far the highest-volume target in real usage, its
    /// Enter behaviour is user-configurable, and Scribe already sends line breaks as Shift+Enter,
    /// which Teams treats as a soft newline. Flattening it is therefore the right answer only for
    /// people whose configuration or client build still submits, so it must be a deliberate choice
    /// rather than something bundled into a broader preset.
    /// </summary>
    public static Preset Teams => new(
        "Only if Teams still sends your dictation early. Teams usually accepts Shift+Enter as a line break, so try it without this first.",
        new AppProfile
        {
            Name = "Microsoft Teams",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
            ProcessNames = ["ms-teams", "Teams"],
        });

    /// <summary>
    /// Documents, where paragraph breaks are the whole point. Explicit rather than implied, so a
    /// user who has set the global mode to always flatten still gets real paragraphs in Word.
    /// </summary>
    public static Preset Documents => new(
        "Word, Excel, PowerPoint, OneNote and Notepad. Keeps paragraph breaks even when your Advanced setting uses one line.",
        new AppProfile
        {
            Name = "Documents",
            NewlineHandling = NewlineInjectionMode.KeepNewlines,
            ProcessNames = ["WINWORD", "EXCEL", "POWERPNT", "ONENOTE", "Notepad", "wordpad"],
        });

    /// <summary>All presets, in the order the settings menu offers them.</summary>
    public static IReadOnlyList<Preset> All { get; } =
        [TerminalsAndShells, AiChatAndAgents, Teams, IdeIntegratedTerminals, Documents];

    /// <summary>
    /// Returns a fresh copy so the caller can edit the added profile without mutating the shared
    /// template (the presets are static, and the settings editor writes straight into the row).
    /// </summary>
    public static AppProfile Instantiate(Preset preset) => new()
    {
        Name = preset.Profile.Name,
        WritingStyle = preset.Profile.WritingStyle,
        NewlineHandling = preset.Profile.NewlineHandling,
        TextFormat = preset.Profile.TextFormat,
        InjectionMethod = preset.Profile.InjectionMethod,
        ShiftEnterLineBreaks = preset.Profile.ShiftEnterLineBreaks,
        ProcessNames = [.. preset.Profile.ProcessNames],
    };
}
