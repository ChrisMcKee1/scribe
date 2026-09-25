using Scribe.Core.Cleanup;

namespace Scribe.Core.Tray;

public enum TrayCommand
{
    RestartToUpdate,
    Settings,
    AddToDictionary,
    CopyLastDictation,
    CopyRecentDictation,
    OpenHistory,
    Microphone,
    AiCleanup,
    SetUpAiCleanup,
    PauseDictation,
    Quit,
}

public enum TrayItemKind
{
    Command,
    Separator,
    Submenu,
    Check,
}

public sealed record TrayMenuItem(
    TrayItemKind Kind,
    string Label,
    TrayCommand? Command = null,
    bool Enabled = true,
    bool IsChecked = false,
    bool IsDefault = false,
    char? AccessKey = null,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static TrayMenuItem Separator { get; } = new(TrayItemKind.Separator, string.Empty, Enabled: false);
}

public sealed record TrayMenuState(
    bool UpdateReady = false,
    bool HasRecentDictation = false,
    TrayAiCleanupItem AiCleanup = default,
    bool DictationPaused = false,
    IReadOnlyList<string>? RecentDictationPreviews = null);

public sealed record TrayMenu(IReadOnlyList<TrayMenuItem> Items)
{
    public static TrayMenu Build(TrayMenuState state)
    {
        var items = new List<TrayMenuItem>();
        if (state.UpdateReady)
        {
            items.Add(new TrayMenuItem(TrayItemKind.Command, "Restart to update Scribe", TrayCommand.RestartToUpdate, AccessKey: 'U'));
            items.Add(TrayMenuItem.Separator);
        }

        items.Add(new TrayMenuItem(TrayItemKind.Command, "Settings", TrayCommand.Settings, IsDefault: true, AccessKey: 'S'));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(TrayItemKind.Command, "Add to dictionary...", TrayCommand.AddToDictionary, AccessKey: 'A'));
        items.Add(new TrayMenuItem(TrayItemKind.Command, "Copy last dictation", TrayCommand.CopyLastDictation, state.HasRecentDictation, AccessKey: 'C'));
        items.Add(new TrayMenuItem(TrayItemKind.Submenu, "Copy a recent dictation", TrayCommand.CopyRecentDictation, AccessKey: 'R', Children: RecentChildren(state)));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(TrayItemKind.Submenu, "Microphone", TrayCommand.Microphone, AccessKey: 'M'));
        items.Add(AiCleanupItem(state.AiCleanup));
        items.Add(new TrayMenuItem(TrayItemKind.Check, "Pause dictation", TrayCommand.PauseDictation, IsChecked: state.DictationPaused, AccessKey: 'P'));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(TrayItemKind.Command, "Quit Scribe", TrayCommand.Quit, AccessKey: 'Q'));
        return new TrayMenu(items);
    }

    private static IReadOnlyList<TrayMenuItem> RecentChildren(TrayMenuState state)
    {
        var previews = state.RecentDictationPreviews ?? [];
        if (previews.Count == 0)
        {
            return [new TrayMenuItem(TrayItemKind.Command, "No recent dictations", Enabled: false)];
        }

        var children = previews.Take(5)
            .Select(preview => new TrayMenuItem(TrayItemKind.Command, preview, TrayCommand.CopyRecentDictation))
            .ToList();
        children.Add(TrayMenuItem.Separator);
        children.Add(new TrayMenuItem(TrayItemKind.Command, "Open history", TrayCommand.OpenHistory));
        return children;
    }

    private static TrayMenuItem AiCleanupItem(TrayAiCleanupItem item) => item.Kind switch
    {
        TrayAiCleanupKind.SetUp => new TrayMenuItem(TrayItemKind.Command, item.Label, TrayCommand.SetUpAiCleanup, item.Enabled, AccessKey: 'I'),
        _ => new TrayMenuItem(TrayItemKind.Check, item.Label, TrayCommand.AiCleanup, item.Enabled, item.Checked, AccessKey: 'I'),
    };
}
