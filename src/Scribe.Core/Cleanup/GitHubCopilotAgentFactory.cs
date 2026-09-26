namespace Scribe.Core.Cleanup;

/// <summary>
/// Builds the cleanup agent for the GitHub Copilot provider through the typed SDK surface.
/// </summary>
/// <remarks>
/// <para>
/// A class of its own, referenced only from <c>TextCleanupService.InitGitHubCopilotAsync</c>, so the
/// Copilot assemblies still load only for a user who selects this provider: the CLR resolves a type
/// the first time code that names it is compiled, and nothing on the startup path names this class.
/// </para>
/// <para>
/// The session configuration is exactly what Agent Framework's convenience overload built
/// (Agent Framework dotnet-1.20.0, GitHubCopilotAgent.GetSessionConfig): the instructions as an
/// appended system message, no tools, and no permission handler, so nothing in the Copilot runtime's
/// coding-agent toolset is approved. The one addition is <c>Model</c>, which the pinned SDK forwards
/// into the create-session request; a blank model stays null and leaves the choice to the CLI, as before.
/// </para>
/// <para>
/// <see cref="GitHubCopilotCleanupAgent"/> runs it rather than Agent Framework's agent, so the
/// session's creation and its send are each handed over through the library vocabulary's admission
/// point (contract 2.10). The agent never owns the client: the client is released with the service,
/// and an owning agent would dispose it every time a setting changed.
/// </para>
/// </remarks>
internal static class GitHubCopilotAgentFactory
{
    /// <summary>A fresh configuration per call, so no two agents share a mutable instance.</summary>
    internal static GitHub.Copilot.SessionConfig BuildSessionConfig(string instructions, string? model) => new()
    {
        Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
        SystemMessage = new GitHub.Copilot.SystemMessageConfig
        {
            Mode = GitHub.Copilot.SystemMessageMode.Append,
            Content = instructions,
        },
    };

    /// <summary>An agent over the shared client, which the service keeps and releases.</summary>
    internal static Microsoft.Agents.AI.AIAgent Create(
        GitHub.Copilot.CopilotClient client, string instructions, string? model, string name) =>
        new GitHubCopilotCleanupAgent(client, BuildSessionConfig(instructions, model), name);
}
