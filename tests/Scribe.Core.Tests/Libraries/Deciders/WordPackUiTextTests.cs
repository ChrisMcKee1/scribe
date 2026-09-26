using Scribe.Core.Libraries;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackUiTextTests
{
    [Fact]
    public void List_meta_uses_source_words_unsaved_and_matches()
    {
        Assert.Equal("Built-in, 94 words", WordPackUiText.ListMeta(true, 94, false));
        Assert.Equal("Imported, 1 word, Unsaved, 1 match", WordPackUiText.ListMeta(false, 1, true, 1));
        Assert.Equal("Imported, 2 words, Unsaved", WordPackUiText.ListMeta(false, 2, true, 0));
    }

    [Fact]
    public void Header_meta_includes_category_and_unsaved_state()
    {
        Assert.Equal("Built-in, Data and AI, 197 words, Unsaved", WordPackUiText.HeaderMeta(true, "Data and AI", 197, true));
    }

    [Fact]
    public void Ai_help_names_the_recipient()
    {
        Assert.Equal(
            "Sends this word pack's words as vocabulary with every AI cleanup request, whether or not you say them.",
            WordPackUiText.AiHelp(true, foundryLocal: false));
        Assert.Equal(
            "Not sent as vocabulary. Words you dictate still reach the AI service in the text.",
            WordPackUiText.AiHelp(false, foundryLocal: false));
        Assert.Equal("AI cleanup runs on this PC, so nothing leaves it.", WordPackUiText.AiHelp(false, foundryLocal: true));
    }

    [Theory]
    [InlineData(TermMarker.NotUsed, "Not used")]
    [InlineData(TermMarker.Update, "Update")]
    [InlineData(TermMarker.OffHere, "Off here")]
    [InlineData(TermMarker.Check, "Check")]
    [InlineData(TermMarker.Removes, "Removes")]
    [InlineData(TermMarker.Changed, "Changed")]
    public void Marker_label_is_the_one_word_status(TermMarker marker, string expected)
    {
        Assert.Equal(expected, WordPackUiText.MarkerLabel(marker));
    }

    [Fact]
    public void Word_pack_text_columns_stay_text_columns_for_typing_tab()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        Assert.Contains("<DataGridTextColumn x:Name=\"LibraryTermSpokenColumn\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<DataGridTextColumn x:Name=\"LibraryTermWrittenColumn\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CellStyle=\"{StaticResource WordPackWrittenCell}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataGridTemplateColumn x:Name=\"LibraryTermWrittenColumn\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_pack_layout_uses_the_measured_tab_content_width_and_root_height()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.WordPacks.cs"));
        var layout = Body(source, "private void ApplyWordPackLayout()");
        Assert.Contains("WordPackLayoutRoot()", layout, StringComparison.Ordinal);
        Assert.Contains("SectionWordPacks.ActualWidth", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("SectionWordPacks.ActualHeight", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_pack_import_dialog_choice_reaches_the_workspace()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.WordPacks.cs"));

        Assert.Contains("accepted.Choice", source, StringComparison.Ordinal);
        Assert.Contains("ImportConflictChoice.UseFilesVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyImport(accepted.Plan, ImportConflictChoice.KeepMine)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_word_pack_notice_action_has_a_page_handler()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.WordPacks.cs"));

        foreach (var action in Enum.GetValues<WordPackNoticeAction>())
        {
            Assert.Contains($"case WordPackNoticeAction.{action}:", source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("word details edits in place", "ApplyWordDetailsEdit", "UpdateSelectedLibraryDirtyState")]
    [InlineData("rename failure keeps selection", "FinishPendingLibraryRename", "LibraryDetailRenameBox.Focus")]
    [InlineData("word pack AI uses current switch and saved provider", "AiCleanupCheck.IsChecked == true", "_savedAiProvider == CleanupProvider.FoundryLocal")]
    [InlineData("short details is a subpage", "ApplyWordDetailsComposition", "WordDetailsBackButton.Visibility")]
    [InlineData("stacked back is on visible card", "WordPacksCardBackButton.Visibility = Visibility.Visible", "WordPacksListColumn.MinWidth = 0")]
    [InlineData("search add recomputes search", "ApplyLibrarySearch();", "RefreshTermRows(_selectedLibraryId);")]
    [InlineData("word use goes through workspace command", "_wordPackWorkspace.SetTermEnabled", "nameof(LibraryTermRow.Enabled)")]
    [InlineData("list and header stay in sync", "SyncWordPackHeader", "LibraryUseCheck.IsChecked")]
    [InlineData("built-in add asks for spoken first", "AskForWordPackSpokenFormAsync", "Scribe hears")]
    [InlineData("preview keys catch handled grid keys", "LibraryTermsGrid_PreviewKeyDown", "LibraryGrid_PreviewKeyDown")]
    [InlineData("word grid checkbox gets first click", "DataGridCheckBoxClick.Attach(LibraryTermsGrid)", "DataGridTypingTab.Attach(LibraryTermsGrid)")]
    [InlineData("word details text boxes are named", "AutomationProperties.LabeledBy=\"{Binding ElementName=WordDetailsSpokenTitle}\"", "AutomationProperties.LabeledBy=\"{Binding ElementName=WordDetailsWrittenTitle}\"")]
    [InlineData("selected no-match text avoids duplicate pack names", "No matches in {selected}. Found in:", "matches.Count > 5")]
    [InlineData("word pack search box can shrink", "MinWidth=\"180\"", "LibrarySearchBox")]
    [InlineData("short card scrolls when grid keeps four rows", "VerticalScrollBarVisibility=\"Auto\"", "LibraryTermsGrid.MinHeight")]
    public void Astra_review_wiring_items_stay_fixed(string item, string first, string second)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.WordPacks.cs"));
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var combined = source + Environment.NewLine + xaml;

        Assert.True(combined.Contains(first, StringComparison.Ordinal), item);
        Assert.True(combined.Contains(second, StringComparison.Ordinal), item);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{signature} has no end.");
        return source[start..end];
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
