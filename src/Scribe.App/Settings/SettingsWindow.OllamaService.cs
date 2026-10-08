using System.Windows;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    public OllamaServiceController? OllamaService { get; init; }
    private readonly CancellationTokenSource _ollamaServiceReads = new();
    private OllamaServiceState? _ollamaServiceState;
    private bool _ollamaServiceBusy;
    private int _ollamaServiceReadVersion;

    private async Task RefreshOllamaServiceAsync()
    {
        if (_closed || SelectedLocalApp != LocalServerApp.Ollama || OllamaService is null || _ollamaServiceBusy)
        {
            return;
        }

        var version = ++_ollamaServiceReadVersion;
        try
        {
            var state = await OllamaService.ReadAsync(_ollamaServiceReads.Token);
            if (!_closed && version == _ollamaServiceReadVersion)
            {
                ShowOllamaService(state);
            }
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception failure)
        {
            TryLog(failure, "Could not read Ollama's service state.");
            if (!_closed)
            {
                ShowOllamaService(new(false, false, "Scribe couldn't check Ollama. Try Start Ollama."));
            }
        }
    }

    private void ShowOllamaService(OllamaServiceState state)
    {
        _ollamaServiceState = state;
        OllamaServiceButton.Content = state.ButtonText;
        OllamaServiceButton.IsEnabled = !_ollamaServiceBusy && state.CanAct;
        OllamaServiceStatusText.Text = state.Description;
    }

    private async void OllamaServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _ollamaServiceBusy || OllamaService is null || _ollamaServiceState?.CanAct == false)
        {
            return;
        }

        var stopping = _ollamaServiceState is { Running: true, Owned: true };
        if (stopping && !await ConfirmRiskyAsync(
            "Stop Ollama?",
            "This stops the Ollama instance Scribe started, including any model download. AI cleanup and other apps using it won't get answers until it starts again.",
            "Stop Ollama"))
        {
            return;
        }

        if (_closed)
        {
            return;
        }

        _ollamaServiceBusy = true;
        ++_ollamaServiceReadVersion;
        OllamaServiceButton.IsEnabled = false;
        OllamaServiceStatusText.Text = stopping ? "Stopping Ollama..." : "Starting Ollama...";
        try
        {
            if (stopping)
            {
                CancelOllamaDownload();
            }

            var state = stopping
                ? await OllamaService.StopAsync().WaitAsync(_ollamaServiceReads.Token)
                : await OllamaService.StartAsync().WaitAsync(_ollamaServiceReads.Token);
            if (_closed)
            {
                return;
            }

            ShowOllamaService(state);
            AnnounceFrom(OllamaServiceStatusText, state.Description);
            await RefreshLocalAppAsync();
            if (!_closed && !stopping && state.Running && state.Error is null)
            {
                var reapplied = StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary);
                await reapplied.Vocabulary;
            }
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception failure)
        {
            TryLog(failure, "Ollama service action failed.");
            if (!_closed)
            {
                OllamaServiceStatusText.Text = "Scribe couldn't change Ollama's state. Check its Windows app and try again.";
            }
        }
        finally
        {
            _ollamaServiceBusy = false;
            if (!_closed)
            {
                await RefreshOllamaServiceAsync();
            }
        }
    }
}
