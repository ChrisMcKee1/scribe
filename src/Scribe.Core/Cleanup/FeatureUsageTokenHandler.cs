using Microsoft.Agents.AI;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Takes Agent Framework's feature-usage token off the User-Agent of every request a cleanup client sends.
/// </summary>
/// <remarks>
/// <para>
/// <c>AsAIAgent</c> registers two User-Agent policies on the OpenAI client it wraps (Agent Framework's
/// <c>OpenAIUserAgentPolicies</c>, unchanged from 1.24.0 to 1.27.0): one appends <c>agent-framework-dotnet/&lt;version&gt;</c>,
/// the other a <c>(feat=v1.&lt;hex&gt;)</c> comment, a bitmask of the framework features this process has used, kept as
/// usage telemetry for Microsoft (the framework's ADR 0033, "Feature-usage bitmask in the User-Agent"). Both apply to HTTPS
/// requests to <c>openai.azure.com</c>, <c>cognitiveservices.azure.com</c> and <c>services.ai.azure.com</c> hosts, which is
/// where every Microsoft Foundry request goes. The framework's one switch is <c>AGENT_FRAMEWORK_FEATURE_MASK_DISABLED</c>, a
/// process-wide variable read once when its type loads and inherited by every child process Scribe starts, so the token is
/// removed on Scribe's own transport instead: per client, after every pipeline policy has run, on each attempt and retry.
/// The framework's name and version stay, and are disclosed with the OpenAI client's headers
/// (<see cref="CleanupDisclosure.AiClientRequestMetadata"/>).
/// </para>
/// <para>
/// The removal is the framework's own (<see cref="FeatureUsage.ApplyToUserAgent"/> with the token excluded), so it matches
/// exactly the comment the framework writes. FeatureUsageTokenWireTests reads the User-Agent on the wire of the production
/// transport, and pins that the packages add the token on their own, which is what makes that test measure this handler.
/// </para>
/// </remarks>
internal sealed class FeatureUsageTokenHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string UserAgent = "User-Agent";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RemoveFeatureToken(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RemoveFeatureToken(request);
        return base.Send(request, cancellationToken);
    }

    internal static void RemoveFeatureToken(HttpRequestMessage request)
    {
        // The pipeline transport adds the header unvalidated, so it is read the same way, never re-parsed into products.
        if (!request.Headers.NonValidated.TryGetValues(UserAgent, out var values))
        {
            return;
        }

        var sent = string.Join(' ', values);
#pragma warning disable MAAI001 // Experimental: the framework's own remover, so a change to its token's shape moves with it.
        var kept = FeatureUsage.ApplyToUserAgent(sent, includeFeatureToken: false);
#pragma warning restore MAAI001
        if (string.Equals(sent, kept, StringComparison.Ordinal))
        {
            return;
        }

        request.Headers.Remove(UserAgent);
        if (kept.Length > 0)
        {
            request.Headers.TryAddWithoutValidation(UserAgent, kept);
        }
    }
}
