using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Windows;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;

namespace Scribe.App.Settings;

/// <summary>
/// The GitHub Copilot provider's own corner of Settings.
/// </summary>
/// <remarks>
/// Split into its own file because this provider needs something none of the others do: it depends
/// on an external CLI that Scribe cannot install as part of itself, so the panel has to report
/// whether that dependency is present and offer to fix it. Keeping that here leaves the main
/// settings file about settings.
/// </remarks>
public partial class SettingsWindow
{
    /// <summary>What the last detection found, so the save path can warn without re-probing.</summary>
    private GitHubCopilotCliStatus _copilotCli = GitHubCopilotCliStatus.Missing;

    private bool _copilotChecked;

    /// <summary>
    /// Whether the model list has already been fetched for this window.
    /// </summary>
    /// <remarks>
    /// Listing spawns the Copilot CLI and waits on it, so it is not free. Selecting the provider
    /// fetches once; flipping to another provider and back reuses what was already read. The refresh
    /// button ignores this, because the reason to press it is that the answer has changed.
    /// </remarks>
    private bool _copilotModelsLoaded;

    /// <summary>
    /// Re-detects the CLI and rewrites the banner.
    /// </summary>
    /// <remarks>
    /// The browse path only locates the executable on disk. The version probe shells out and is
    /// reserved for explicit actions, or for the saved active provider that may already contact GitHub.
    /// <para>
    /// The close-while-in-flight case is handled by <see cref="System.Windows.Threading.DispatcherOperation"/>
    /// rather than by a null check on the control. A field generated from <c>x:Name</c> is not set
    /// back to null when a window closes, so testing it proves nothing; and a blocking
    /// <c>Dispatcher.Invoke</c> on a shut-down dispatcher throws before any such check could run,
    /// into a continuation whose task is discarded. <c>InvokeAsync</c> queues instead of throwing,
    /// and <c>HasShutdownStarted</c> is the condition that actually distinguishes the two cases.
    /// </para>
    /// </remarks>
    private void RefreshCopilotCliStatus(bool runVersionProbe = false, bool allowModelList = false)
    {
        _copilotChecked = false;
        CopilotLoadModelsButton.IsEnabled = false;
        UpdateAiEnabledState();
        var authorizedSettings = _committedSettings.Clone();
        var authorizedDraft = CurrentAiDraftSettings();

        _ = Task.Run(() => runVersionProbe ? GitHubCopilotCli.Detect() : GitHubCopilotCli.Locate()).ContinueWith(
            task =>
            {
                var status = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : GitHubCopilotCliStatus.Missing;

                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                _ = Dispatcher.InvokeAsync(() =>
                {
                    _copilotCli = status;
                    _copilotChecked = true;
                    CopilotLoadModelsButton.IsEnabled = status.Found;
                    UpdateAiEnabledState();

                    var authorization = status.Found && allowModelList
                        ? RemoteActivityPolicy.CaptureAutomaticContact(authorizedSettings, authorizedDraft, RemoteActivityTrigger.WindowOpen, status.Path)
                        : null;
                    if (status.Found && !_copilotModelsLoaded &&
                        RemoteActivityPolicy.IsStillAuthorized(authorization, _committedSettings, CurrentAiDraftSettings(), status.Path))
                    {
                        LoadCopilotModels();
                    }
                    else if (!status.Found)
                    {
                        _copilotModelsLoaded = false;
                    }
                });
            },
            TaskScheduler.Default);
    }

    private void CopilotRecheckButton_Click(object sender, RoutedEventArgs e) => RefreshCopilotCliStatus(runVersionProbe: true);

    /// <summary>
    /// Hands the install to WinGet, in a terminal the user can see.
    /// </summary>
    /// <remarks>
    /// Deliberately not a silent background install. This puts software on the user's machine and
    /// then needs an interactive GitHub sign-in, so it runs visibly in a console they can read,
    /// answer prompts in, and cancel. Scribe does not elevate and does not pipe the output: an
    /// install that fails should fail where its own error message is visible, not inside a status
    /// line here.
    /// </remarks>
    private void CopilotInstallButton_Click(object sender, RoutedEventArgs e)
    {
        // `winget install --id GitHub.Copilot` is the same command the CLI's own docs give, and the
        // upgrade form is a no-op on a machine that does not have it, which is why one button can
        // read either way without branching on state that may have changed since detection.
        var command = _copilotCli.Found
            ? "winget upgrade --id GitHub.Copilot --accept-source-agreements"
            : "winget install --id GitHub.Copilot --accept-source-agreements --accept-package-agreements";

        if (!TryRunInTerminal(command, "Couldn't start the installer."))
        {
            return;
        }

        ShowInfo("The installer is open. Finish it, then choose Check again.");
        _copilotChecked = true;
        UpdateAiEnabledState();
        RefreshCopilotCliStatus(runVersionProbe: true);
    }

    /// <summary>
    /// Opens the CLI interactively so the user can complete the GitHub sign-in.
    /// </summary>
    /// <remarks>
    /// Sign-in is a browser OAuth flow the CLI drives itself. Scribe never sees the token, never
    /// stores one, and has no reason to: the SDK reads whatever the CLI already holds.
    /// </remarks>
    private void CopilotSignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_copilotCli.Found || _copilotCli.Path is null)
        {
            return;
        }

        if (TryRunInTerminal($"\"{_copilotCli.Path}\"", "Couldn't start GitHub Copilot."))
        {
            ShowInfo("Sign in there if prompted, then close it and choose Get models.");
            _copilotChecked = true;
            UpdateAiEnabledState();
        }
    }

    /// <summary>
    /// Reads the models this GitHub account is licensed for, and their reasoning levels.
    /// </summary>
    /// <remarks>
    /// This is the one backend that can answer the question properly. Azure Foundry publishes a
    /// per-deployment capability map with no reasoning information in it, so the Foundry model
    /// picker cannot say what efforts a deployment accepts. The Copilot SDK's <c>ModelInfo</c>
    /// carries <c>SupportedReasoningEfforts</c> and <c>DefaultReasoningEffort</c>, so the list here
    /// is discovered rather than hardcoded and cannot go stale as GitHub changes its line-up.
    /// </remarks>
    private void CopilotLoadModelsButton_Click(object sender, RoutedEventArgs e)
    {
        // An explicit press always re-reads: the reason to use it is that installing or signing in
        // has changed what the account can see since the automatic fetch ran.
        _copilotModelsLoaded = false;
        LoadCopilotModels();
    }

    /// <summary>
    /// Fetches the model list and binds it to the combo box. Safe to call when the CLI is missing;
    /// it does nothing.
    /// </summary>
    private void LoadCopilotModels()
    {
        if (!_copilotCli.Found || _copilotCli.Path is null || _copilotModelsLoaded)
        {
            return;
        }

        // Set before the work starts, not after it succeeds: this is a re-entry guard, and detection
        // can fire again while a fetch is still in flight.
        _copilotModelsLoaded = true;
        CopilotLoadModelsButton.IsEnabled = false;
        CopilotModelHint.Text = "Reading the models your Copilot subscription allows...";

        var cliPath = _copilotCli.Path;
        _ = Task.Run(async () => await GitHubCopilotModels.ListAsync(cliPath, CancellationToken.None)
                .ConfigureAwait(false))
            .ContinueWith(
                task =>
                {
                    var models = task.Status == TaskStatus.RanToCompletion
                        ? task.Result
                        : [];

                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }

                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        CopilotLoadModelsButton.IsEnabled = true;

                        if (models.Count == 0)
                        {
                            // Released so the next selection tries again rather than inheriting a
                            // failure the user may have already fixed by signing in.
                            _copilotModelsLoaded = false;
                            CopilotModelHint.Text =
                                "Could not read the model list. Leave the box blank to use your account's " +
                                "default, or type a model id such as gpt-5 or claude-sonnet-4.";
                            return;
                        }

                        // The typed value is preserved across a refresh: the box is editable, and
                        // wiping a deliberate choice because the list arrived would be the kind of
                        // small theft that makes a settings page feel untrustworthy.
                        var typed = CopilotModelCombo.Text;
                        CopilotModelCombo.ItemsSource = models.Select(m => m.Id).ToArray();
                        CopilotModelCombo.Text = typed;

                        CopilotModelHint.Text =
                            $"{models.Count} model(s) available. Leave empty to use your account's default.";
                    });
                },
                TaskScheduler.Default);
    }

    /// <summary>
    /// Runs one command in a visible console. Returns false and reports on the banner when it cannot.
    /// </summary>
    private bool TryRunInTerminal(string command, string failureTitle)
    {
        try
        {
            // cmd /k rather than /c: the window stays open so the user can read the result and answer
            // any prompt, which is the entire reason for showing a terminal instead of hiding one.
            _ = Process.Start(new ProcessStartInfo("cmd.exe", $"/k {command}")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not launch a terminal for the Copilot CLI ({Failure}).", FailureShape.Describe(ex));
            ShowInfo(failureTitle + " Run it yourself in a command window.", InfoBarSeverity.Error);
            return false;
        }
    }
}
