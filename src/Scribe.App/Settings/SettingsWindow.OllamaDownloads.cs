using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Scribe.App.Infrastructure;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private CancellationTokenSource? _ollamaDownload;
    private bool _ollamaModelSwitching;

    private void UpdateOllamaDownloadUi()
    {
        if (OllamaDownloadPanel is null || _closed)
        {
            return;
        }

        var shown = SelectedLocalApp == LocalServerApp.Ollama;
        OllamaServicePanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (OllamaService is null)
        {
            OllamaServiceButton.IsEnabled = false;
        }

        OllamaDownloadPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            CancelOllamaDownload();
        }

        var busy = _ollamaDownload is not null;
        OllamaDownloadModelBox.IsEnabled = !busy;
        OllamaDownloadButton.IsEnabled = !busy;
        OllamaDownloadCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OllamaDownloadCancelButton.IsEnabled = !_ollamaModelSwitching;
    }

    private void OllamaDownloadModelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && !_imeComposing)
        {
            e.Handled = true;
            OllamaDownloadButton_Click(sender, new RoutedEventArgs());
        }
    }

    private async void OllamaDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _ollamaDownload is not null || SelectedLocalApp != LocalServerApp.Ollama)
        {
            return;
        }

        var request = OllamaModelDownload.Validate(OllamaDownloadModelBox.Text);
        if (request.Model is not { } model)
        {
            ShowOllamaDownloadMessage(request.Error!, announce: true);
            OllamaDownloadModelBox.Focus();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var restoreFocus = OllamaDownloadModelBox.IsKeyboardFocusWithin || OllamaDownloadButton.IsKeyboardFocusWithin;
        _ollamaDownload = cancellation;
        UpdateOllamaDownloadUi();
        if (restoreFocus && IsActive)
        {
            OllamaDownloadCancelButton.Focus();
        }

        ShowOllamaDownloadMessage("Ollama is preparing the download...", announce: true);
        OllamaDownloadStage? lastStage = null;
        var progress = new Progress<OllamaDownloadProgress>(value =>
        {
            if (_closed || !ReferenceEquals(_ollamaDownload, cancellation) || cancellation.IsCancellationRequested)
            {
                return;
            }

            var announce = lastStage != value.Stage;
            lastStage = value.Stage;
            ShowOllamaDownloadMessage(OllamaModelDownload.Describe(value), announce);
            OllamaDownloadProgressBar.Visibility = value.Percent is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
            OllamaDownloadProgressBar.Value = Math.Clamp(value.Percent.GetValueOrDefault(), 0, 100);
        });

        try
        {
            using (var downloader = new OllamaModelDownloader(_log))
            {
                await downloader.DownloadAsync(model, progress, cancellation.Token);
            }

            if (_closed || cancellation.IsCancellationRequested)
            {
                return;
            }

            OllamaDownloadProgressBar.Visibility = Visibility.Collapsed;
            var state = await RefreshLocalAppAsync(preserveEmptySelection: true);
            if (_closed || cancellation.IsCancellationRequested || SelectedLocalApp != LocalServerApp.Ollama)
            {
                return;
            }

            if (OllamaModelDownload.CompletionProblem(state, model) is { } problem)
            {
                ShowOllamaDownloadMessage(problem, announce: true);
                return;
            }

            while (_saveInProgress && !_closed)
            {
                await Task.Delay(100, cancellation.Token);
            }

            if (_closed || cancellation.IsCancellationRequested)
            {
                return;
            }

            var useModel = await ShowConfirmationAsync(ThemedConfirmation.Create(
                "Use the downloaded model?",
                OllamaModelDownload.SwitchQuestion(model),
                "Use model",
                cancelIsDefault: true,
                cancelText: "Keep current model"));
            if (_closed || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (useModel)
            {
                await SaveDownloadedOllamaChoiceAsync(model);
            }
            else
            {
                ShowOllamaDownloadMessage("Downloaded. Your model choice hasn't changed.", announce: true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed)
            {
                ShowOllamaDownloadMessage(OllamaModelDownload.Canceled, announce: true);
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Ollama model download failed.");
            if (!_closed)
            {
                ShowOllamaDownloadMessage(OllamaModelDownload.Failure(ex), announce: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_ollamaDownload, cancellation))
            {
                _ollamaDownload = null;
            }

            if (!_closed)
            {
                OllamaDownloadProgressBar.Visibility = Visibility.Collapsed;
                UpdateOllamaDownloadUi();
                if (restoreFocus && IsActive &&
                    (Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, this) ||
                        ReferenceEquals(Keyboard.FocusedElement, OllamaDownloadCancelButton)))
                {
                    OllamaDownloadButton.Focus();
                }
            }
        }
    }

    private async Task SaveDownloadedOllamaChoiceAsync(string model)
    {
        var shownBefore = SelectedLocalAppModel;
        var rememberedBefore = _chosenLocalAppModels.GetValueOrDefault(LocalServerApp.Ollama);
        _saveInProgress = true;
        _ollamaModelSwitching = true;
        UpdateOllamaDownloadUi();
        var saved = false;
        try
        {
            var stored = await Task.Run(() =>
                _settingsRepository.Update(settings => OllamaModelDownload.ApplyChoice(settings, model)));
            saved = true;
            OllamaModelDownload.CopyChoice(stored, _settings);
            OllamaModelDownload.CopyChoice(stored, _committedSettings);
            _savedAiProvider = stored.AiCleanupProvider;
            if (_closed)
            {
                return;
            }

            // The confirmed choice is saved, but an edit made while its write waited is still the user's draft.
            if (string.Equals(
                rememberedBefore, _chosenLocalAppModels.GetValueOrDefault(LocalServerApp.Ollama), StringComparison.Ordinal))
            {
                _chosenLocalAppModels[LocalServerApp.Ollama] = model;
            }

            if (SelectedLocalApp == LocalServerApp.Ollama &&
                string.Equals(shownBefore, SelectedLocalAppModel, StringComparison.Ordinal))
            {
                SetLocalAppModelItems(LocalServerApp.Ollama, _localAppState?.Models ?? [], model);
            }

            CancelCleanupConnectionTest();
            UpdateDictionaryGlossaryHint();
            ShowLocalAppStatus();
            RefreshFooterNow();

            var reapplied = StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary);
            var refresh = await reapplied.Vocabulary;
            if (!_closed)
            {
                ShowOllamaDownloadMessage(
                    refresh.Applied
                        ? OllamaModelDownload.Saved
                        : VocabularyNotice.SavedButNotApplied("Model choice saved"),
                    announce: true);
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not save or apply the downloaded Ollama model choice.");
            if (!_closed)
            {
                ShowOllamaDownloadMessage(
                    saved
                        ? "Model choice saved, but it isn't in use yet. Restart Scribe to apply it."
                        : "The model is downloaded, but its choice couldn't be saved. Your saved model hasn't changed. Try again after saving Settings.",
                    announce: true);
            }
        }
        finally
        {
            _ollamaModelSwitching = false;
            _saveInProgress = false;
            ScheduleFooterRefresh();
        }
    }

    private void OllamaDownloadCancelButton_Click(object sender, RoutedEventArgs e) => CancelOllamaDownload();

    private void CancelOllamaDownload()
    {
        if (!_ollamaModelSwitching)
        {
            _ollamaDownload?.Cancel();
        }
    }

    private void ShowOllamaDownloadMessage(string message, bool announce)
    {
        OllamaDownloadStatusText.Text = message;
        OllamaDownloadStatusText.Visibility = Visibility.Visible;
        if (announce)
        {
            AnnounceFrom(OllamaDownloadStatusText, message);
        }
    }
}
