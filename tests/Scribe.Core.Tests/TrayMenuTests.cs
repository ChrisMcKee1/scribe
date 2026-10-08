using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class TrayMenuTests
{
    [Fact]
    public void Builds_root_in_binding_order_with_exact_separators()
    {
        var menu = TrayMenu.Build(State(updateReady: true, hasRecent: true));

        Assert.Equal(
            ["Restart to update Scribe", "", "Settings", "", "Add to dictionary...", "Copy last dictation", "Copy a recent dictation", "", "Microphone", "AI cleanup", "Pause dictation", "Use plain text once", "", "Quit Scribe"],
            menu.Items.Select(i => i.Label));
        Assert.Equal([1, 3, 7, 12], menu.Items.Select((item, index) => (item, index)).Where(pair => pair.item.Kind == TrayItemKind.Separator).Select(pair => pair.index));
    }

    [Fact]
    public void Update_item_is_conditional()
    {
        Assert.Contains(TrayMenu.Build(State(updateReady: true)).Items, item => item.Command == TrayCommand.RestartToUpdate);
        Assert.DoesNotContain(TrayMenu.Build(State(updateReady: false)).Items, item => item.Command == TrayCommand.RestartToUpdate);
        Assert.DoesNotContain(TrayMenu.Build(State(updateReady: false)).Items.Take(1), item => item.Kind == TrayItemKind.Separator);
    }

    [Fact]
    public void Settings_is_the_only_default_item()
    {
        var defaults = TrayMenu.Build(State()).Items.Where(item => item.IsDefault).ToArray();

        var item = Assert.Single(defaults);
        Assert.Equal(TrayCommand.Settings, item.Command);
    }

    [Fact]
    public void No_help_or_learn_rows_or_promotion_items_appear()
    {
        var labels = TrayMenu.Build(State(updateReady: true)).Items.Select(i => i.Label).ToArray();

        Assert.DoesNotContain(labels, label => label.Contains("Help", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, label => label.Contains("Learn", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, label => label.Contains("welcome", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, label => label.Contains("Store", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, label => label.Contains("Share", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Copy_last_dictation_is_disabled_without_recent_dictations()
    {
        var absent = TrayMenu.Build(State(hasRecent: false)).Items.Single(item => item.Command == TrayCommand.CopyLastDictation);
        var present = TrayMenu.Build(State(hasRecent: true)).Items.Single(item => item.Command == TrayCommand.CopyLastDictation);

        Assert.False(absent.Enabled);
        Assert.True(present.Enabled);
    }

    [Fact]
    public void Access_keys_are_unique_within_root_menu()
    {
        var keys = TrayMenu.Build(State(updateReady: true)).Items.Where(i => i.AccessKey is not null).Select(i => i.AccessKey!.Value).ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(['U', 'S', 'A', 'C', 'R', 'M', 'I', 'P', 'T', 'Q'], keys);
    }

    [Fact]
    public void No_separator_is_first_last_or_doubled()
    {
        var items = TrayMenu.Build(State(updateReady: true)).Items;

        Assert.NotEqual(TrayItemKind.Separator, items[0].Kind);
        Assert.NotEqual(TrayItemKind.Separator, items[^1].Kind);
        for (var i = 1; i < items.Count; i++)
        {
            Assert.False(items[i].Kind == TrayItemKind.Separator && items[i - 1].Kind == TrayItemKind.Separator);
        }
    }

    [Fact]
    public void Labels_have_no_dashes_or_ampersands()
    {
        foreach (var item in TrayMenu.Build(State(updateReady: true)).Items.Concat(TrayMenu.Build(State(recentPreviews: ["one"])).Items.SelectMany(i => i.Children ?? [])))
        {
            Assert.DoesNotContain('&', item.Label);
            Assert.DoesNotContain('\u2013', item.Label);
            Assert.DoesNotContain('\u2014', item.Label);
        }
    }

    [Fact]
    public void Ai_cleanup_variants_choose_command_or_check_item()
    {
        var setup = TrayMenu.Build(State(ai: new TrayAiCleanupItem(TrayAiCleanupKind.SetUp, "Set up AI cleanup...", false, true))).Items.Single(i => i.Command == TrayCommand.SetUpAiCleanup);
        var off = TrayMenu.Build(State(ai: new TrayAiCleanupItem(TrayAiCleanupKind.Toggle, "AI cleanup", false, true))).Items.Single(i => i.Command == TrayCommand.AiCleanup);
        var notReady = TrayMenu.Build(State(ai: new TrayAiCleanupItem(TrayAiCleanupKind.Toggle, "AI cleanup (not ready)", true, true))).Items.Single(i => i.Command == TrayCommand.AiCleanup);

        Assert.Equal(TrayItemKind.Command, setup.Kind);
        Assert.Equal(TrayItemKind.Check, off.Kind);
        Assert.False(off.IsChecked);
        Assert.True(notReady.IsChecked);
        Assert.Equal("AI cleanup (not ready)", notReady.Label);
    }

    [Fact]
    public void Recent_submenu_lists_previews_and_open_history_or_empty_state()
    {
        var full = TrayMenu.Build(State(recentPreviews: ["one", "two", "three", "four", "five", "six"])).Items.Single(i => i.Command == TrayCommand.CopyRecentDictation).Children!;
        var empty = TrayMenu.Build(State(recentPreviews: [])).Items.Single(i => i.Command == TrayCommand.CopyRecentDictation).Children!;

        Assert.Equal(["one", "two", "three", "four", "five", "", "Open history"], full.Select(i => i.Label));
        Assert.Equal("No recent dictations", Assert.Single(empty).Label);
    }

    private static TrayMenuState State(bool updateReady = false, bool hasRecent = true, TrayAiCleanupItem? ai = null, IReadOnlyList<string>? recentPreviews = null) =>
        new(updateReady, hasRecent, ai ?? new TrayAiCleanupItem(TrayAiCleanupKind.Toggle, "AI cleanup", Checked: false, Enabled: true), DictationPaused: false, recentPreviews ?? ["one"]);
}
