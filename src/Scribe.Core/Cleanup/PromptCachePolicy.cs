using System.ClientModel;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Scribe.Core.Cleanup;

/// <summary>
/// <c>AppSettings.AiCleanupPromptCaching</c> on the wire, for Microsoft Foundry only. On is the request Scribe has always
/// sent, so the service's own prompt caching applies. Off adds <c>prompt_cache_options</c> set to
/// <c>{"mode":"explicit"}</c>, with no breakpoint anywhere in the request, to every Microsoft Foundry request on both
/// surfaces: the readiness probe, Test connection, each dictation and each one-off completion. Microsoft documents the
/// explicit mode as "Azure OpenAI uses only explicit breakpoints for cache reads and writes. If the request contains no
/// explicit breakpoints, it doesn't use prompt caching or incur cache-write charges", for GPT-5.6 and later models on
/// standard deployments (learn.microsoft.com/azure/foundry/openai/how-to/prompt-caching).
/// </summary>
/// <remarks>
/// The same page's FAQ, "Can I disable prompt caching?": "On Standard pay-as-you-go deployments with GPT-5.6 models and
/// later model families, set <c>prompt_cache_options.mode</c> to <c>explicit</c> and don't add any explicit breakpoints. The
/// request doesn't use prompt caching or incur cache-write charges. Earlier models and PTU-M deployments don't support this
/// option; prompt caching remains enabled by default." And under "Configure prompt cache breakpoints": "Models before the
/// GPT-5.6 family don't support <c>prompt_cache_options</c> or <c>prompt_cache_breakpoint</c>. Requests that include these
/// parameters return a <c>400</c> error." A 400 that names the field, from an earlier model or a provisioned deployment, is
/// reported by this cause (<see cref="DescribeRejection"/>), and no path sends the request again without the option: a
/// deployment that cannot turn its cache off leaves AI cleanup unavailable, and Scribe types what it hears.
/// </remarks>
internal static class PromptCachePolicy
{
    /// <summary>The request field the setting controls.</summary>
    internal const string FieldName = "prompt_cache_options";

    /// <summary>What the field carries when caching is off: the explicit mode, with no breakpoint in the request.</summary>
    internal const string ExplicitWithoutBreakpoints = "{\"mode\":\"explicit\"}";

    /// <summary>The setting's name, as Settings shows it.</summary>
    internal const string SettingName = CleanupDisclosure.PromptCachingTitle;

    /// <summary>The cause when a deployment names the option in its refusal.</summary>
    internal static CleanupReason Rejected { get; } = CleanupReason.Same(
        "This deployment can't turn caching off. Turn \"" + SettingName + "\" back on, or use a GPT-5.6 or later model on " +
        "a Standard deployment.");

    private const string PossiblyRejected =
        "Microsoft Foundry rejected the request (400). With caching off, this can mean this deployment can't turn caching " +
        "off: turn \"" + SettingName + "\" back on, or use a GPT-5.6 or later model on a Standard deployment.";

    private static ReadOnlySpan<byte> OptionsPath => "$.prompt_cache_options"u8;

    private static ReadOnlySpan<byte> OptionsValue => "{\"mode\":\"explicit\"}"u8;

#pragma warning disable SCME0001, OPENAI001
    /// <summary>Asks the Responses surface not to read or write the prompt cache.</summary>
    internal static void TurnOff(CreateResponseOptions options) => options.Patch.Set(OptionsPath, OptionsValue);

    /// <summary>Asks the Chat Completions surface not to read or write the prompt cache.</summary>
    internal static void TurnOff(ChatCompletionOptions options) => options.Patch.Set(OptionsPath, OptionsValue);
#pragma warning restore SCME0001, OPENAI001

    /// <summary>
    /// The cause of a failure made with caching off: <see cref="Rejected"/> when a 400 names the option, a 400 that names
    /// nothing as possibly this cause (Azure words the same refusal several ways, see <c>IsSurfaceRejection</c>), and null
    /// for anything else, which keeps its usual description. The service's own text is shown, never logged.
    /// </summary>
    internal static CleanupReason? DescribeRejection(Exception? exception, string serverDetail)
    {
        if (FindBadRequest(exception) is not { } badRequest)
        {
            return null;
        }

        if (NamesTheOption(badRequest))
        {
            return Rejected;
        }

        return new CleanupReason(
            PossiblyRejected,
            string.IsNullOrEmpty(serverDetail) ? PossiblyRejected : PossiblyRejected + " " + serverDetail);
    }

    /// <summary>True when a failure is a 400 whose body names the option: the deployment cannot turn its cache off.</summary>
    internal static bool IsRejection(Exception? exception) =>
        FindBadRequest(exception) is { } badRequest && NamesTheOption(badRequest);

    // Depth-bounded like CleanupFailureShape: a failure path must not be able to overflow the stack.
    private static ClientResultException? FindBadRequest(Exception? exception)
    {
        var depth = 0;
        for (var current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (current is ClientResultException { Status: 400 } badRequest)
            {
                return badRequest;
            }
        }

        return null;
    }

    // Never throws: the raw response may be unbuffered or disposed by now, and this runs while reporting a failure.
    private static bool NamesTheOption(ClientResultException exception)
    {
        try
        {
            var body = exception.GetRawResponse()?.Content?.ToString();
            return (body is not null && body.Contains(FieldName, StringComparison.OrdinalIgnoreCase))
                || exception.Message.Contains(FieldName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
