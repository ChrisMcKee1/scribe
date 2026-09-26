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
    // What Save stores for the snippets, and exactly which rows it stored. Validation has already blocked every changed
    // incomplete row, so the incomplete rows left are untouched placeholders, which are dropped and stay new, and stored
    // rows validation calls unchanged, which are kept exactly as stored so saving another snippet can't delete them. A
    // complete row always saves its current text verbatim: validation's trimmed comparison would call a line break added
    // at the start or end of the text unchanged, and a snippet types its text exactly.
    private List<Snippet> BuildSnippets(out SnippetRow? duplicate, out IReadOnlyList<SnippetSubmission> submission)
    {
        var rows = _snippetRows.ToList();
        var result = SnippetBuilder.Build([.. rows.Select(ToBuilderRow)]);
        duplicate = result.HasDuplicate ? rows[result.DuplicateIndex] : null;
        submission = [.. result.IncludedRows.Select((index, built) => new SnippetSubmission(
            rows[index],
            result.Snippets[built].Phrase,
            result.Snippets[built].Template,
            result.Snippets[built].Enabled))];
        return [.. result.Snippets];
    }

    private static SnippetBuilder.Row ToBuilderRow(SnippetRow row) =>
        IsIncomplete(row) && SettingsDraftValidator.IsUnchanged(ToDraftRow(row))
            ? new SnippetBuilder.Row(row.Id, row.LoadedPhrase, row.LoadedTemplate, row.LoadedEnabled, KeepAsStored: true)
            : new SnippetBuilder.Row(row.Id, row.Phrase, row.Template, row.Enabled);

    private static bool IsIncomplete(SnippetRow row) =>
        string.IsNullOrWhiteSpace(row.Phrase) || string.IsNullOrWhiteSpace(row.Template);

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

    // What a Save stored for each row it stored, so those rows can adopt exactly that as their saved baseline once it is
    // committed. A row it didn't store (an untouched placeholder) stays new, and an edit made after the Save read the rows
    // isn't in what was stored, so it stays unsaved.
    private sealed record SnippetSubmission(SnippetRow Row, string Phrase, string Template, bool Enabled);

    // The Save stored the submitted rows, so each one still in the list takes what was submitted as its saved baseline, in
    // memory, as the profile rows do. Nothing is read back from storage: a failed or slow read after a committed Save once
    // emptied the list, or replaced edits made meanwhile, and a retried Save then stored that. A new row keeps Id 0, which
    // is safe: SnippetRepository.SaveAll keeps rows by id, deletes the rest and inserts rows with no id, so the next Save
    // replaces the row this one inserted instead of duplicating it.
    private void MarkSnippetRowsSaved(IReadOnlyList<SnippetSubmission> submission)
    {
        foreach (var submitted in submission)
        {
            if (!_snippetRows.Contains(submitted.Row))
            {
                continue;
            }

            submitted.Row.Origin = DraftRowOrigin.Saved;
            submitted.Row.LoadedPhrase = submitted.Phrase;
            submitted.Row.LoadedTemplate = submitted.Template;
            submitted.Row.LoadedEnabled = submitted.Enabled;
        }

        RefreshSnippetEmptyState();
    }

    private IReadOnlyList<SnippetDraftRow> SnippetDraftRows() =>
        _snippetLoad.IsLoaded ? [.. _snippetRows.Select(ToDraftRow)] : [];

    private static SnippetDraftRow ToDraftRow(SnippetRow row) => new(
        RowKey: row.RowKey,
        Origin: row.Origin,
        Touched: row.Touched,
        Phrase: row.Phrase,
        Template: row.Template,
        LoadedPhrase: row.LoadedPhrase,
        LoadedTemplate: row.LoadedTemplate,
        Enabled: row.Enabled,
        LoadedEnabled: row.LoadedEnabled);
}
