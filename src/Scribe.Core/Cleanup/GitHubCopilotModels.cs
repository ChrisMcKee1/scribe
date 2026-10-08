namespace Scribe.Core.Cleanup;

/// <summary>
/// One model a GitHub Copilot account is licensed for.
/// </summary>
/// <param name="Id">The id to pass as the model, e.g. <c>gpt-5</c>.</param>
/// <param name="Name">Display name, when the runtime supplies one.</param>
/// <param name="SupportedReasoningEfforts">
/// Reasoning levels this model accepts. Empty when it is not a reasoning model or does not say.
/// </param>
/// <param name="DefaultReasoningEffort">The level used when none is requested, when known.</param>
public sealed record GitHubCopilotModel(
    string Id,
    string? Name,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort);

/// <summary>
/// Reads the model list from the GitHub Copilot CLI.
/// </summary>
/// <remarks>
/// This exists so the settings window can offer a real list instead of a free-text box, and it is
/// worth noting how unusual that is here. Azure Foundry publishes a per-deployment capability map
/// (<c>chatCompletion</c>, <c>responses</c>, <c>realtime</c>) with nothing in it about reasoning, so
/// the Foundry picker genuinely cannot say which efforts a deployment takes or what it defaults to.
/// The Copilot SDK's own model metadata carries both, so for this one provider the answer is
/// discoverable and a hardcoded table would be strictly worse.
///
/// Kept out of <see cref="TextCleanupService"/> and behind a plain DTO on purpose: the App layer asks
/// this question from the settings window, and it should not have to reference the Copilot SDK to
/// hear the answer. Every SDK name stays inside one method body, for the same lazy-loading reason
/// the cleanup path does it.
/// </remarks>
public static class GitHubCopilotModels
{
    /// <summary>
    /// Lists the models available to the signed-in account, or an empty list when the CLI cannot
    /// answer. Never throws: an unreadable list is a reason to fall back to free text, not an error
    /// to show a user who was only opening Settings.
    /// The query waits at most 60 seconds, followed by the owner's at most eight seconds of shutdown waits.
    /// </summary>
    /// <param name="cliPath">Path to the Copilot CLI, from <see cref="GitHubCopilotCli.Detect"/>.</param>
    /// <param name="cancellationToken">Cancellation for the whole probe.</param>
    public static Task<IReadOnlyList<GitHubCopilotModel>> ListAsync(
        string cliPath, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(cliPath)
            ? Task.FromResult<IReadOnlyList<GitHubCopilotModel>>([])
            : ListCoreAsync(cliPath, null, TimeProvider.System, cancellationToken);

    internal static Task<IReadOnlyList<GitHubCopilotModel>> ListForRuntimeAsync(
        string runtimeUrl, TimeProvider timeProvider, CancellationToken cancellationToken = default) =>
        ListCoreAsync(null, runtimeUrl, timeProvider, cancellationToken);

    private static async Task<IReadOnlyList<GitHubCopilotModel>> ListCoreAsync(
        string? cliPath, string? runtimeUrl, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // Bounded independently of the caller: this runs from a button in Settings, and a CLI that
        // never answers must not leave that button disabled for the life of the window.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), timeProvider);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        GitHubCopilotClientLifetime? owner = null;
        try
        {
            var client = new GitHub.Copilot.CopilotClient(new GitHub.Copilot.CopilotClientOptions
            {
                // The user's own installed CLI, exactly as the cleanup path does it. Scribe ships no
                // Copilot runtime of its own (see CopilotSkipCliDownload in Directory.Build.props).
                Connection = runtimeUrl is null
                    ? GitHub.Copilot.RuntimeConnection.ForStdio(cliPath!)
                    : GitHub.Copilot.RuntimeConnection.ForUri(runtimeUrl),
            });
            var clientOwner = new GitHubCopilotClientLifetime(client, timeProvider: timeProvider);
            owner = clientOwner;

            var token = cts.Token;
            var listing = Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await clientOwner.RunAsync(() => client.StartAsync(token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return await clientOwner.RunAsync(() => client.ListModelsAsync(token)).ConfigureAwait(false);
            });
            _ = listing.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var models = await listing.WaitAsync(token).ConfigureAwait(false);

            return [.. models
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new GitHubCopilotModel(
                    m.Id,
                    m.Name,
                    m.SupportedReasoningEfforts is { } efforts
                        ? [.. efforts.Where(e => !string.IsNullOrWhiteSpace(e))]
                        : [],
                    m.DefaultReasoningEffort))];
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            if (owner is not null)
            {
                try
                {
                    await owner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A session that will not close cleanly must not turn a successful list into a
                    // failure, nor leave the exception to surface from a finally block.
                }
            }
        }
    }
}
