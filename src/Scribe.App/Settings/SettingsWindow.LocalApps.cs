using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

/*
 * "On this PC" runs the AI with Scribe's own runtime (Foundry Local) or with Ollama or LM Studio, which many people
 * already have. Ollama and LM Studio are saved as the service at their own address (LocalAiServer.AddressOf), so an
 * older Scribe reads the same settings as "another AI service" and keeps working; Settings recognizes only those exact
 * addresses, saved without a key, as the apps (CustomServiceFields.SavedApp), so a server set up by hand, or one that
 * needs a key, stays under "Another AI service" with its key. Another AI service the user had set up is remembered
 * while an app is chosen, so choosing it again brings back its address, model and key. The model list, the memory line
 * and Free memory come from the app itself (LocalServerClient), over this PC only, and carry nothing the user said.
 */
public partial class SettingsWindow
{
    private readonly LocalServerClient _localServers = new();
    private readonly Dictionary<LocalServerApp, string> _chosenLocalAppModels = [];
    private LocalServerState? _localAppState;
    private LocalServerApp _localAppStateFor;
    private LocalServerApp _localAppModelsFor;
    private int _localAppReadVersion;
    private bool _localAppReading;
    private bool _localAppBusy;

    /// <summary>A model in the app's list: its name for requests and what the app calls it.</summary>
    private sealed record LocalAppModelItem(string Id, string Label);

    /// <summary>Ollama or LM Studio when "On this PC" runs the AI with one of them; <see cref="LocalServerApp.None"/> otherwise.</summary>
    private LocalServerApp SelectedLocalApp =>
        AiProviderLocalRadio?.IsChecked != true ? LocalServerApp.None :
        LocalAppOllamaRadio?.IsChecked == true ? LocalServerApp.Ollama :
        LocalAppLmStudioRadio?.IsChecked == true ? LocalServerApp.LmStudio :
        LocalServerApp.None;

    // The model chosen in the selected app's list. The list always belongs to the selected app (ResetLocalAppModels), so a
    // model of the app chosen before is never stored with this one's address.
    private string? SelectedLocalAppModel =>
        SelectedLocalApp is var app && app != LocalServerApp.None && _localAppModelsFor == app
            ? (LocalAppModelBox?.SelectedItem as LocalAppModelItem)?.Id
            : null;

    private (CustomServiceFields.Fields Stored, CustomServiceFields.Fields Remembered) ShownCustomServiceFields =>
        CustomServiceFields.ForSave(
            SelectedLocalApp,
            SelectedLocalAppModel,
            new(CustomEndpointBox?.Text, CustomModelBox?.Text, CustomApiKeyBox?.Password, ChosenCustomApiStyle),
            _committedSettings);

    /// <summary>
    /// The server address, model, key and API Save stores for the OpenAI-compatible service: the app's own address and the
    /// model picked from its list, with no key and Chat Completions, for Ollama or LM Studio; what the boxes hold for another
    /// AI service, with the API they reach it through. The draft, Try dictation and Save read them only through this, so what
    /// is stored is what the page compares.
    /// </summary>
    private (string? Endpoint, string? Model, string? ApiKey, CustomApiStyle ApiStyle) ShownCustomService
    {
        get
        {
            var stored = ShownCustomServiceFields.Stored;
            return (stored.Endpoint, stored.Model, stored.ApiKey, stored.ApiStyle);
        }
    }

    /// <summary>
    /// Another AI service's boxes, remembered beside Ollama or LM Studio; none when another AI service, or anything else,
    /// is chosen. The draft and Save read it only through this.
    /// </summary>
    private CustomServiceFields.Fields ShownRememberedService => ShownCustomServiceFields.Remembered;

    // Shows the saved choice: Ollama or LM Studio at its own address under "On this PC", anything else as before. The boxes
    // show another AI service: the one saved, or the one remembered beside the app.
    private void LoadLocalAppSettings()
    {
        var app = CustomServiceFields.SavedApp(_settings);
        var otherService = CustomServiceFields.OtherService(_settings);
        CustomEndpointBox.Text = otherService.Endpoint ?? string.Empty;
        CustomModelBox.Text = otherService.Model ?? string.Empty;
        CustomApiKeyBox.Password = otherService.ApiKey ?? string.Empty;
        _chosenCustomApiStyle = otherService.ApiStyle;
        ShowCustomApiStyle();
        LocalAppScribeRadio.IsChecked = app == LocalServerApp.None;
        LocalAppOllamaRadio.IsChecked = app == LocalServerApp.Ollama;
        LocalAppLmStudioRadio.IsChecked = app == LocalServerApp.LmStudio;
        if (app != LocalServerApp.None)
        {
            AiProviderLocalRadio.IsChecked = true;
        }

        _chosenLocalAppModels.Clear();
        ResetLocalAppModels(app);
    }

    private void LocalAppRadio_Checked(object sender, RoutedEventArgs e)
    {
        UpdateDictionaryGlossaryHint();
        if (_loadingUi)
        {
            return;
        }

        CancelCleanupConnectionTest();
        ResetLocalAppModels(SelectedLocalApp);
        UpdateAiProviderPanels();
        UpdateAiEnabledState();
        if (SelectedLocalApp != LocalServerApp.None)
        {
            _ = RefreshLocalAppAsync();
        }
        else
        {
            _ = RefreshFoundryModelsAsync(initializeRuntime: false);
        }
    }

    private void LocalAppModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDictionaryGlossaryHint();
        if (_loadingUi)
        {
            return;
        }

        if (SelectedLocalApp is var app && app != LocalServerApp.None && SelectedLocalAppModel is { } model)
        {
            _chosenLocalAppModels[app] = model;
        }

        ShowLocalAppStatus();
    }

    private async void LocalAppStatusRow_ActionInvoked(object? sender, AiCleanupActionId action)
    {
        try
        {
            switch (action)
            {
                case AiCleanupActionId.Unload:
                    await FreeLocalAppMemoryAsync();
                    break;
                default:
                    await RefreshLocalAppAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("A local app action failed ({Failure}).", FailureShape.Describe(ex));
        }
    }

    // Reads the selected app when nothing has been read for it yet and no read is running: turning AI cleanup on shows the
    // app's models rather than a check that never ends.
    private void EnsureLocalAppRead()
    {
        var app = SelectedLocalApp;
        if (app != LocalServerApp.None && !_localAppReading && (_localAppState is null || _localAppStateFor != app))
        {
            _ = RefreshLocalAppAsync();
        }
    }

    // Asks the selected app which models it has and which it holds, then fills the list and the status line. A newer
    // request, or a change of app, makes an older answer drop out.
    private async Task RefreshLocalAppAsync()
    {
        var app = SelectedLocalApp;
        if (app == LocalServerApp.None || LocalAiServer.AddressOf(app) is not { } address)
        {
            return;
        }

        var version = ++_localAppReadVersion;
        _localAppState = null;
        _localAppStateFor = app;
        _localAppReading = true;
        ShowLocalAppStatus();

        LocalServerState state;
        try
        {
            state = await _localServers.ReadAsync(address);
        }
        finally
        {
            if (version == _localAppReadVersion)
            {
                _localAppReading = false;
            }
        }

        if (version != _localAppReadVersion || SelectedLocalApp != app)
        {
            return;
        }

        _localAppState = state;
        _log.LogInformation(
            "Settings read {App}: {Reach}, {Models} model(s), {Loaded} loaded.", app, state.Reach, state.Models.Count, state.Loaded.Count);
        if (state.Reach == LocalServerReach.Reached)
        {
            SetLocalAppModelItems(app, state.Models, SelectedLocalAppModel);
        }

        ShowLocalAppStatus();
    }

    private async Task FreeLocalAppMemoryAsync()
    {
        var app = SelectedLocalApp;
        if (_localAppBusy || app == LocalServerApp.None || LocalAiServer.AddressOf(app) is not { } address ||
            SelectedLocalAppModel is not { } model)
        {
            return;
        }

        _localAppBusy = true;
        LocalAppStatusRow.Show(new(AiCleanupStatusKind.Busy, "Freeing memory..."));
        try
        {
            // Through AI cleanup, so the next dictation readies the model again rather than trusting an earlier answer.
            var freed = await _cleanup.FreeLocalAppModelAsync(address, model);
            _log.LogInformation("Settings asked {App} to free a model's memory: {Freed}.", app, freed);
        }
        finally
        {
            _localAppBusy = false;
        }

        await RefreshLocalAppAsync();
    }

    // The list for app before it answers: the model chosen for it in this window, else the one saved for it, else nothing.
    private void ResetLocalAppModels(LocalServerApp app)
    {
        var chosen = app == LocalServerApp.None
            ? null
            : _chosenLocalAppModels.GetValueOrDefault(app) ??
                (CustomServiceFields.SavedApp(_committedSettings) == app ? CustomServiceFields.SavedAppModel(_committedSettings) : null);
        SetLocalAppModelItems(app, [], chosen);
    }

    private void SetLocalAppModelItems(LocalServerApp app, IReadOnlyList<LocalServerModel> models, string? chosen)
    {
        var (listed, selected) = LocalAppSetup.ModelChoices(models, chosen, RoomyGraphicsCard);
        var items = listed.Select(model => new LocalAppModelItem(
                model.Id,
                string.Equals(model.DisplayName, model.Id, StringComparison.Ordinal) ? model.Id : $"{model.DisplayName} ({model.Id})"))
            .ToList();

        var wasLoading = _loadingUi;
        _loadingUi = true;
        try
        {
            _localAppModelsFor = app;
            LocalAppModelBox.ItemsSource = items;
            LocalAppModelBox.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Id, selected, StringComparison.Ordinal));
        }
        finally
        {
            _loadingUi = wasLoading;
        }
    }

    private void ShowLocalAppStatus()
    {
        UpdateLocalModelTuning();
        var app = SelectedLocalApp;
        if (app == LocalServerApp.None)
        {
            LocalAppStatusRow.Show(null);
            return;
        }

        var state = _localAppStateFor == app ? _localAppState : null;
        LocalAppStatusRow.Show(LocalAppSetup.Describe(app, state, SelectedLocalAppModel, CurrentIdleMinutes()));
    }

    private int CurrentIdleMinutes() =>
        SelectedDurationValue(IdleReleaseCombo, IdleReleaseCustomBox, _settings.ReleaseModelsAfterIdleMinutes);

    // Read once per window: the display adapters do not change while Settings is open.
    private IReadOnlyList<Scribe.Core.Diagnostics.GraphicsAdapter> Adapters =>
        _adapters ??= Scribe.Core.Diagnostics.GraphicsAdapters.Detect();

    private IReadOnlyList<Scribe.Core.Diagnostics.GraphicsAdapter>? _adapters;

    // A graphics card with room for the larger local models (Gemma 4 E4B loads in about 3 GB).
    private bool RoomyGraphicsCard => Adapters.Any(adapter => adapter.HasAtLeast(6));

    // The first time AI cleanup is turned on here, Scribe's own model starts from the one that suits this PC's hardware
    // (CleanupModelCatalog.DefaultAliasFor) rather than the smallest, unless the user already picked another.
    private void ChooseDeviceDefaultModelOnFirstSetUp()
    {
        if (AiCleanupPageState.SavedSetupState(_committedSettings) != AiCleanupSetupState.NothingConfigured ||
            !string.Equals(SelectedFoundryModelAlias, CleanupModelCatalog.DefaultAlias, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var alias = CleanupModelCatalog.DefaultAliasFor(Adapters);
        if (!string.Equals(alias, CleanupModelCatalog.DefaultAlias, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("First AI cleanup setup on this PC starts from its larger model (graphics card with room for it).");
            SetFoundryModelItems([], alias);
            UpdateAiModelHint();
        }
    }
}
