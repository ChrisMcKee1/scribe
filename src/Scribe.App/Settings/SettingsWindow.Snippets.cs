using System.Windows;
using System.Windows.Controls;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private void InitializeSnippetList()
    {
        SnippetList.ItemsSource = _snippetRows;
        _snippetEmptyText = SnippetEmptyHint.Text;
        SnippetEmptyHint.Text = "Loading snippets...";
        SnippetEmptyState.Visibility = Visibility.Visible;
        SetSnippetsEditable(false);
        RefreshTextChangesNotice();
    }

    // Same reason as the dictionary: an edit before the rows arrive would be saved as a deletion.
    private void SetSnippetsEditable(bool editable)
    {
        SnippetList.IsEnabled = editable;
        SnippetAddButton.IsEnabled = editable;
        SnippetEmptyActionButton.IsEnabled = editable || _snippetLoad.State == SettingsSectionState.Failed;
        RefreshSnippetCommands();
    }

    private async void LoadSnippetsAsync()
    {
        if (!_snippetLoad.TryBegin(SnippetSignature(), out var ticket))
        {
            return;
        }

        IReadOnlyList<Snippet> snippets;
        try
        {
            snippets = await Task.Run(() => _snippets.GetAll());
        }
        catch (Exception ex)
        {
            if (_snippetLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load snippets for Settings.");
                SnippetEmptyHint.Text = "Couldn't load your snippets.";
                SnippetEmptyActionButton.Content = "Try again";
                SnippetEmptyActionButton.Visibility = Visibility.Visible;
                SnippetEmptyState.Visibility = Visibility.Visible;
                SetSnippetsEditable(false);
            }

            return;
        }

        if (!_snippetLoad.CanPublish(ticket))
        {
            return;
        }

        _snippetRows.Clear();
        foreach (var snippet in snippets)
        {
            _snippetRows.Add(new SnippetRow
            {
                Id = snippet.Id,
                Phrase = snippet.Phrase,
                Template = snippet.Template,
                Enabled = snippet.Enabled,
                Origin = DraftRowOrigin.Saved,
                LoadedPhrase = snippet.Phrase,
                LoadedTemplate = snippet.Template,
                LoadedEnabled = snippet.Enabled,
            });
        }

        _snippetLoad.Publish(ticket, SnippetSignature());
        SnippetEmptyActionButton.Content = "Add snippet";
        SnippetEmptyHint.Text = _snippetEmptyText;
        SetSnippetsEditable(true);
        RefreshSnippetEmptyState();
    }

    private SnippetRow? SelectedSnippet => SnippetList.SelectedItem as SnippetRow;

    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearSnippetValidation();
        var row = SelectedSnippet;
        SnippetEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshSnippetEmptyState();
        RefreshSnippetCommands();
        if (row is null)
        {
            return;
        }

        _loadingSnippet = true;
        try
        {
            SnippetPhraseBox.Text = row.Phrase;
            SnippetTemplateBox.Text = row.Template;
            SnippetEnabledCheck.IsChecked = row.Enabled;
            SnippetEditorTitle.Text = row.PrimaryText;
        }
        finally
        {
            _loadingSnippet = false;
        }
    }

    private void SnippetAddButton_Click(object sender, RoutedEventArgs e)
    {
        var row = new SnippetRow { Phrase = string.Empty, Template = string.Empty, Origin = DraftRowOrigin.New };
        _snippetRows.Add(row);
        SnippetList.SelectedItem = row;
        SnippetList.ScrollIntoView(row);
        SnippetPhraseBox.Focus();
        SnippetPhraseBox.SelectAll();
        RefreshSnippetEmptyState();
        RefreshSnippetCommands();
    }

    private void SnippetEmptyActionButton_Click(object sender, RoutedEventArgs e)

    {

        if (_snippetLoad.State == SettingsSectionState.Failed)

        {

            SnippetEmptyHint.Text = "Loading snippets...";

            SnippetEmptyActionButton.Visibility = Visibility.Collapsed;

            LoadSnippetsAsync();

            return;

        }



        SnippetAddButton_Click(sender, e);

    }



    private async void SnippetDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSnippet is not { } row)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(row.Template))
        {
            var confirmed = await ConfirmRiskyAsync(
                "Delete this snippet?",
                "Nothing changes until you save.",
                "Delete snippet");
            if (!confirmed)
            {
                return;
            }
        }

        var index = SnippetList.SelectedIndex;
        _snippetRows.Remove(row);
        if (_snippetRows.Count > 0)
        {
            SnippetList.SelectedIndex = Math.Min(index, _snippetRows.Count - 1);
        }

        RefreshSnippetEmptyState();
        RefreshSnippetCommands();
    }

    private void SnippetPhraseBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSnippet && SelectedSnippet is { } row)
        {
            row.Phrase = SnippetPhraseBox.Text;
            row.Touched = true;
            SnippetEditorTitle.Text = row.PrimaryText;
            HideValidation(SnippetPhraseValidation, SnippetPhraseValidationText, SnippetPhraseBox);
        }
    }

    private void SnippetTemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSnippet && SelectedSnippet is { } row)
        {
            row.Template = SnippetTemplateBox.Text;
            row.Touched = true;
            HideValidation(SnippetTemplateValidation, SnippetTemplateValidationText, SnippetTemplateBox);
        }
    }

    private void SnippetEnabledCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_loadingSnippet && SelectedSnippet is { } row)
        {
            row.Enabled = SnippetEnabledCheck.IsChecked == true;
            row.Touched = true;
        }
    }

    /// <summary>
    /// Builds the desired snippet state from the editor rows, skipping rows with a blank phrase or
    /// template. Validation has already selected any typed-but-incomplete row before this runs.
    /// </summary>
    private List<Snippet> BuildSnippets(out SnippetRow? duplicate)
    {
        var result = SnippetBuilder.Build(
            _snippetRows.Select(r => new SnippetBuilder.Row(
                r.Id, r.Phrase, r.Template, r.Enabled)).ToList());

        duplicate = result.HasDuplicate ? _snippetRows[result.DuplicateIndex] : null;
        return result.Snippets.ToList();
    }

    private void RefreshSnippetEmptyState()
    {
        var hasSelection = SelectedSnippet is not null;
        SnippetEditor.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SnippetEmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        if (hasSelection)
        {
            return;
        }

        if (_snippetRows.Count == 0 && _snippetLoad.IsLoaded)
        {
            SnippetEmptyHint.Text = "No snippets yet. A snippet types saved text when you say its phrase.";
            SnippetEmptyActionButton.Visibility = Visibility.Visible;
        }
        else if (_snippetLoad.State == SettingsSectionState.Loading)
        {
            SnippetEmptyHint.Text = "Loading snippets...";
            SnippetEmptyActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SnippetEmptyHint.Text = "Select a snippet to edit it.";
            SnippetEmptyActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshSnippetCommands()
    {
        SnippetDeleteButton.IsEnabled = _snippetLoad.IsLoaded && SelectedSnippet is not null;
    }

    private void ClearSnippetValidation()
    {
        HideValidation(SnippetPhraseValidation, SnippetPhraseValidationText, SnippetPhraseBox);
        HideValidation(SnippetTemplateValidation, SnippetTemplateValidationText, SnippetTemplateBox);
    }

    private void StartSnippetRowsRefreshAfterSave(string storedSignature)

    {

        _ = RefreshSnippetRowsFromStorageAfterSaveAsync(storedSignature);

    }



    private async Task RefreshSnippetRowsFromStorageAfterSaveAsync(string storedSignature)

    {

        IReadOnlyList<Snippet> stored;

        try

        {

            stored = await Task.Run(() => _snippets.GetAll());

        }

        catch (Exception ex)

        {

            TryLog(ex, "Could not read snippets after saving Settings.");

            _snippetLoad.MarkFailedAfterSave(storedSignature);

            SnippetList.SelectedItem = null;

            SnippetEmptyHint.Text = "Couldn't load your snippets.";

            SnippetEmptyActionButton.Content = "Try again";

            SnippetEmptyActionButton.Visibility = Visibility.Visible;

            SnippetEmptyState.Visibility = Visibility.Visible;

            SetSnippetsEditable(false);

            return;

        }



        var selectedPhrase = SelectedSnippet?.Phrase;

        _snippetRows.Clear();

        foreach (var snippet in stored)

        {

            _snippetRows.Add(new SnippetRow

            {

                Id = snippet.Id,

                Phrase = snippet.Phrase,

                Template = snippet.Template,

                Enabled = snippet.Enabled,

                Origin = DraftRowOrigin.Saved,

                LoadedPhrase = snippet.Phrase,

                LoadedTemplate = snippet.Template,

                LoadedEnabled = snippet.Enabled,

            });

        }



        SnippetEmptyActionButton.Content = "Add snippet";

        _snippetLoad.MarkSaved(SnippetSignature());

        SnippetList.SelectedItem = _snippetRows.FirstOrDefault(row => string.Equals(row.Phrase, selectedPhrase, StringComparison.OrdinalIgnoreCase));

        SetSnippetsEditable(true);

        RefreshSnippetEmptyState();

    }



    private IReadOnlyList<SnippetDraftRow> SnippetDraftRows() =>
        _snippetLoad.IsLoaded
            ? _snippetRows.Select(row => new SnippetDraftRow(
                RowKey: row.RowKey,
                Origin: row.Origin,
                Touched: row.Touched,
                Phrase: row.Phrase,
                Template: row.Template,
                LoadedPhrase: row.LoadedPhrase,
                LoadedTemplate: row.LoadedTemplate,
                Enabled: row.Enabled,
                LoadedEnabled: row.LoadedEnabled)).ToList()
            : [];
}
