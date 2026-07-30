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
    /// <summary>A template: the profile itself plus a one-line explanation for the menu.</summary>
    public readonly record struct Preset(string Description, AppProfile Profile);

    /// <summary>
    /// Terminals and shells. Enter submits the command line, so a dictation the AI cleanup split
    /// into paragraphs would run several partial commands. The plain-text writing style is safe
    /// here because a shell has no use for markdown, bullets or code fences.
    /// </summary>
    public static Preset TerminalsAndShells => new(
        "Windows Terminal, PowerShell, cmd and other shells. Keeps a dictation on one line so it is not run early, and asks AI cleanup for plain text.",
        new AppProfile
        {
            Name = "Terminals and shells",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
            WritingStyle = "Plain text only. No markdown, no bullet points, no headings and no code fences.",
            ProcessNames =
            [
                "WindowsTerminal", "wt", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
                "alacritty", "wezterm-gui", "ConEmu64", "mintty", "Hyper", "Tabby", "warp",
                "kitty", "putty",
            ],
        });

    /// <summary>
    /// Editors and IDEs, kept separate from terminals and deliberately blunt about the trade-off.
    /// A process name cannot tell an editor pane from an integrated terminal, so this profile
    /// applies to BOTH, and it exists only for people who dictate into the integrated terminal
    /// often enough to accept losing line breaks in the editor. No writing style is attached: an
    /// editor is exactly where markdown and code fences are wanted.
    /// </summary>
    public static Preset IdeIntegratedTerminals => new(
        "For dictating into an IDE's integrated terminal. Warning: a process name cannot tell the terminal from the editor, so this removes line breaks in your source files too.",
        new AppProfile
        {
            Name = "IDE integrated terminals",
            NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
            ProcessNames =
            [
                "Code", "Code - Insiders", "VSCodium", "cursor", "windsurf", "devenv",
                "rider64", "idea64", "pycharm64", "goland64", "clion64", "webstorm64",
                "sublime_text", "notepad++", "zed",
            ],
        });

    /// <summary>
    /// Desktop AI assistants and chat clients whose composer sends on Enter. This is the preset
    /// that addresses the reported failure: AI cleanup introduces paragraph breaks that raw
    /// recognition never contains, and each one submitted a partial message.
    /// </summary>
    public static Preset AiChatAndAgents => new(
        "Claude, ChatGPT, GitHub Copilot, Microsoft 365 Copilot and Scout. Stops a multi-paragraph dictation being sent as several separate messages.",
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
        "Only if Teams still sends your dictation early. Teams normally accepts Shift+Enter as a line break, so try it without this first.",
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
        "Word, Excel, PowerPoint, OneNote and Notepad. Keeps paragraph breaks even when the global setting flattens them.",
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
        ProcessNames = [.. preset.Profile.ProcessNames],
    };
}
