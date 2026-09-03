using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Windows;
using Scribe.Core.Cleanup;
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

    /// <summary>
    /// Re-detects the CLI and rewrites the banner.
    /// </summary>
    /// <remarks>
    /// Detection shells out to read a version, so it runs off the UI thread and comes back through
    /// the dispatcher.
    /// <para>
    /// The close-while-in-flight case is handled by <see cref="System.Windows.Threading.DispatcherOperation"/>
    /// rather than by a null check on the control. A field generated from <c>x:Name</c> is not set
    /// back to null when a window closes, so testing it proves nothing; and a blocking
    /// <c>Dispatcher.Invoke</c> on a shut-down dispatcher throws before any such check could run,
    /// into a continuation whose task is discarded. <c>InvokeAsync</c> queues instead of throwing,
    /// and <c>HasShutdownStarted</c> is the condition that actually distinguishes the two cases.
    /// </para>
    /// </remarks>
    private void RefreshCopilotCliStatus()
    {
        CopilotCliBar.Severity = InfoBarSeverity.Informational;
        CopilotCliBar.Title = "Checking for the GitHub Copilot CLI…";
        CopilotCliBar.Message = string.Empty;
        CopilotRecheckButton.IsEnabled = false;

        _ = Task.Run(GitHubCopilotCli.Detect).ContinueWith(
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
                    CopilotRecheckButton.IsEnabled = true;

                    if (status.Found)
                    {
                        CopilotCliBar.Severity = InfoBarSeverity.Success;
                        CopilotCliBar.Title = "GitHub Copilot CLI found";
                        CopilotCliBar.Message = status.Version is { } version
                            ? $"{version} at {status.Path}"
                            : status.Path ?? string.Empty;
                        CopilotInstallButton.Content = "Update the CLI";
                    }
                    else
                    {
                        CopilotCliBar.Severity = InfoBarSeverity.Warning;
                        CopilotCliBar.Title = "GitHub Copilot CLI not found";
                        CopilotCliBar.Message =
                            "This provider runs cleanup through the Copilot CLI. Install it, sign in once, " +
                            "then choose Check again.";
                        CopilotInstallButton.Content = "Install the CLI";
                    }

                    // The model list and the sign-in prompt both need the CLI, so they follow it.
                    CopilotLoadModelsButton.IsEnabled = status.Found;
                    CopilotSignInButton.IsEnabled = status.Found;
                });
            },
            TaskScheduler.Default);
    }

    private void CopilotRecheckButton_Click(object sender, RoutedEventArgs e) => RefreshCopilotCliStatus();

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

        if (!TryRunInTerminal(command, "Could not start the installer."))
        {
            return;
        }

        CopilotCliBar.Severity = InfoBarSeverity.Informational;
        CopilotCliBar.Title = "Installer running";
        CopilotCliBar.Message =
            "Finish in the terminal window, then choose Check again. A new install also needs `copilot` " +
            "run once to sign in.";
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

        if (TryRunInTerminal($"\"{_copilotCli.Path}\"", "Could not start the Copilot CLI."))
        {
            CopilotCliBar.Severity = InfoBarSeverity.Informational;
            CopilotCliBar.Title = "Copilot CLI opened";
            CopilotCliBar.Message = "Sign in there if prompted, then close it and choose List models.";
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
        if (!_copilotCli.Found || _copilotCli.Path is null)
        {
            return;
        }

        CopilotLoadModelsButton.IsEnabled = false;
        CopilotModelHint.Text = "Asking the Copilot CLI which models your licence allows…";

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

                        var withEfforts = models
                            .Where(m => m.SupportedReasoningEfforts.Count > 0)
                            .Select(m => $"{m.Id} ({string.Join('/', m.SupportedReasoningEfforts)})")
                            .Take(4)
                            .ToArray();

                        CopilotModelHint.Text = withEfforts.Length > 0
                            ? $"{models.Count} model(s) available. Reasoning levels: {string.Join(", ", withEfforts)}."
                            : $"{models.Count} model(s) available. Leave blank to use your account's default.";
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
            _log.LogWarning(ex, "Could not launch a terminal for the Copilot CLI.");
            CopilotCliBar.Severity = InfoBarSeverity.Error;
            CopilotCliBar.Title = failureTitle;
            CopilotCliBar.Message = "Run it yourself in a terminal: " + command;
            return false;
        }
    }
}
