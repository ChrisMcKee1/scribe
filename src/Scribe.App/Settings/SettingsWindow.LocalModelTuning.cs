using System.Windows;
using System.Windows.Controls;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

/*
 * Each app on this PC keeps its own tuning (LocalModelTuning): Ollama's context size and whole vocabulary under Ollama
 * settings, LM Studio's under LM Studio settings, and Foundry Local's whole vocabulary under Model settings. Every app has
 * its own controls, shown only while that app runs the AI and folded away until opened, so switching apps never carries
 * one app's tuning to another and Save stores each straight from its own controls. The status line under each says what
 * the model reads, as far as Scribe knows it, and how much the whole vocabulary needs.
 */
public partial class SettingsWindow
{
    // The whole vocabulary's estimated tokens, as the dictionary page composes it (UpdateDictionaryGlossaryHint).
    private long _wholeVocabularyTokens;

    private int SelectedOllamaContextTokens =>
        (OllamaContextSizeCombo?.SelectedItem as ComboBoxItem)?.Tag is int tokens ? tokens : 0;

    private int SelectedLmStudioContextTokens =>
        (LmStudioContextSizeCombo?.SelectedItem as ComboBoxItem)?.Tag is int tokens ? tokens : 0;

    // Shows the saved tuning of every app; each app's controls are its own.
    private void LoadLocalModelTuning()
    {
        FillContextSizes(OllamaContextSizeCombo, "Ollama", _settings.AiCleanupOllamaContextTokens);
        FillContextSizes(LmStudioContextSizeCombo, "LM Studio", _settings.AiCleanupLmStudioContextTokens);
        OllamaWholeVocabularyCheck.IsChecked = _settings.AiCleanupOllamaSendWholeVocabulary;
        LmStudioWholeVocabularyCheck.IsChecked = _settings.AiCleanupLmStudioSendWholeVocabulary;
        FoundryWholeVocabularyCheck.IsChecked = _settings.AiCleanupFoundryLocalSendWholeVocabulary;
        UpdateLocalModelTuning();
    }

    private static void FillContextSizes(ComboBox combo, string appName, int stored)
    {
        combo.ItemsSource = LocalModelTuningText.ContextSizes(appName, stored)
            .Select(size => new ComboBoxItem { Content = size.Label, Tag = size.Tokens })
            .ToList();
        combo.SelectedItem = (combo.ItemsSource as IEnumerable<ComboBoxItem>)?
            .FirstOrDefault(item => item.Tag is int tokens && tokens == stored)
            ?? (combo.ItemsSource as IEnumerable<ComboBoxItem>)?.FirstOrDefault();
    }

    private void LocalModelTuning_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLocalModelTuning();
        if (_loadingUi)
        {
            return;
        }

        // The size the model loads at, or how Scribe reaches it, changed: an earlier test no longer vouches for it.
        CancelCleanupConnectionTest();
        UpdateDictionaryGlossaryHint();
    }

    // Which app's settings show, and each status line.
    private void UpdateLocalModelTuning()
    {
        if (OllamaTuningExpander is null || LmStudioTuningExpander is null || FoundryContextStatusText is null)
        {
            return;
        }

        var app = SelectedLocalApp;
        OllamaTuningExpander.Visibility = app == LocalServerApp.Ollama ? Visibility.Visible : Visibility.Collapsed;
        LmStudioTuningExpander.Visibility = app == LocalServerApp.LmStudio ? Visibility.Visible : Visibility.Collapsed;

        var model = SelectedLocalAppModel;
        var state = _localAppStateFor == app ? _localAppState : null;
        var loaded = state?.LoadedFor(model)?.ContextTokens ?? 0;

        // What AI cleanup serves vouches for the size first: Ollama with a size reloads the model at it with Scribe's next
        // request (capped at what the model takes), and with Ollama's setting Scribe's own request can replace another
        // app's copy. Without that, what Ollama holds now, unless Scribe asks for a size of its own.
        var ollamaAsked = SelectedOllamaContextTokens;
        var ollamaInUse = app != LocalServerApp.Ollama
            ? 0
            : ServedLocalContext(LocalServerApp.Ollama, model, ollamaAsked) is > 0 and var servedOllama
                ? servedOllama
                : ollamaAsked > 0 ? 0 : loaded;
        OllamaContextStatusText.Text = LocalModelTuningText.ContextStatus(
            "Ollama", ollamaInUse, ollamaAsked, _wholeVocabularyTokens, VocabularyRoomAt(ollamaInUse > 0 ? ollamaInUse : ollamaAsked));

        // LM Studio: requests reach the copy it holds, so a read that found one says what they read; with no read, the context
        // AI cleanup serves; and with nothing held, nothing is in use and Scribe asks for its size when the model loads.
        var lmStudioAsked = SelectedLmStudioContextTokens;
        var lmStudioInUse = app != LocalServerApp.LmStudio
            ? 0
            : state is { Reach: LocalServerReach.Reached }
                ? loaded
                : ServedLocalContext(LocalServerApp.LmStudio, model, lmStudioAsked);
        LmStudioContextStatusText.Text = LocalModelTuningText.ContextStatus(
            "LM Studio",
            lmStudioInUse,
            lmStudioAsked,
            _wholeVocabularyTokens,
            VocabularyRoomAt(lmStudioInUse > 0 ? lmStudioInUse : lmStudioAsked));

        // Foundry Local says what its model reads only once it serves the saved model.
        var foundryInUse = SavedActiveFoundryModelMatches(SelectedFoundryModelAlias) && _cleanup.Status == CleanupStatus.Ready
            ? _cleanup.LocalContextTokens
            : 0;
        FoundryContextStatusText.Text = LocalModelTuningText.ContextStatus(
            "Foundry Local", foundryInUse, asked: 0, _wholeVocabularyTokens, VocabularyRoomAt(foundryInUse));
    }

    // The context AI cleanup serves, when the saved settings run it on exactly what the page shows (the app, the model and
    // the size asked) and it is ready; 0 otherwise.
    private int ServedLocalContext(LocalServerApp app, string? model, int asked)
    {
        var committed = _committedSettings;
        var committedSize = app == LocalServerApp.Ollama
            ? committed.AiCleanupOllamaContextTokens
            : committed.AiCleanupLmStudioContextTokens;
        return committed.EnableAiCleanup &&
            committed.AiCleanupProvider == CleanupProvider.OpenAiCompatible &&
            LocalAiServer.AppAt(committed.AiCleanupCustomEndpoint) == app &&
            LocalServerClient.SameModel(committed.AiCleanupCustomModel, model) &&
            ContextBudget.Sanitize(committedSize) == ContextBudget.Sanitize(asked) &&
            _cleanup.Status == CleanupStatus.Ready
                ? _cleanup.LocalContextTokens
                : 0;
    }

    // The room the whole vocabulary has beside a typical dictation at a size, under the instructions the page shows; null
    // when the size is not known.
    private int? VocabularyRoomAt(int contextTokens)
    {
        if (contextTokens <= 0 || AiPromptStyleCombo is null)
        {
            return null;
        }

        var options = new CleanupOptions(
            true,
            SelectedProvider,
            SelectedFoundryModelAlias,
            null,
            null,
            WritingStyle: AiWritingStyleBox?.Text,
            CustomEndpoint: SelectedCustomEndpoint,
            CustomModel: SelectedLocalAppModel,
            PromptStyle: SelectedPromptStyle,
            FrontierPrompt: AiFrontierPromptBox?.Text,
            LocalPrompt: AiLocalPromptBox?.Text);
        return ContextBudget.VocabularyRoom(contextTokens, options);
    }
}
