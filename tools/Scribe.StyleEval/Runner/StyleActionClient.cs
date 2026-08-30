using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Scribe.Core.TextActions;
using Scribe.Evals.Benchmark;

namespace Scribe.StyleEval.Runner;

/// <summary>What one model call produced.</summary>
internal sealed record ActionResponse(
    string Text,
    long LatencyMs,
    long? InputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    string? Error);

/// <summary>
/// Runs one shipping text action against the deployment under test.
/// </summary>
/// <remarks>
/// <para>
/// The whole value of this class is what it does NOT do. The system prompt comes from
/// <see cref="TextActionPrompt.BuildSystemPrompt"/>, the user message from
/// <see cref="TextActionPrompt.BuildUserMessage"/>, and the answer goes through
/// <see cref="TextActionSanitizer.Sanitize"/>. All three are the exact code the app runs, referenced
/// from Scribe.Core rather than copied, so the suite can never drift into grading a stale snapshot
/// of the instruction sets.
/// </para>
/// <para>
/// The transport is <c>Scribe.Evals.Benchmark.DirectResponsesCleanupClient</c>, the Azure Responses
/// path the model benchmark already uses. One client per action: the system prompt is fixed for an
/// action once the glossary and house style are fixed, which they are here.
/// </para>
/// </remarks>
internal sealed class StyleActionClient
{
    private readonly Dictionary<string, DirectResponsesCleanupClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _systemPrompts = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    private readonly AdaptiveRateLimiter _limiter;

    public StyleActionClient(
        string endpoint,
        string deployment,
        string? subscription,
        string? tenantId,
        IEnumerable<TextAction> actions,
        ReasoningEffort? reasoning,
        int maxOutputTokens,
        TimeSpan timeout,
        AdaptiveRateLimiter limiter)
    {
        _timeout = timeout;
        _limiter = limiter;
        var credential = BuildCredential(subscription, tenantId);

        foreach (var action in actions)
        {
            // No glossary: the user's dictionary is per-install, and a corpus graded against one
            // person's vocabulary would not be reproducible. The house style is left at the shipping
            // default for the same reason, which is also what an untouched install sends.
            var systemPrompt = TextActionPrompt.BuildSystemPrompt(action);
            _systemPrompts[action.Id] = systemPrompt;
            _clients[action.Id] = new DirectResponsesCleanupClient(
                endpoint,
                deployment,
                tenantId,
                systemPrompt,
                reasoning,
                maxOutputTokens,
                timeout,
                disableRetries: false,
                credential);
        }
    }

    /// <summary>
    /// The exact system prompt an action is sending. Written into the run header so a results file
    /// records the prompts it graded.
    /// </summary>
    public string SystemPromptFor(string actionId) => _systemPrompts[actionId];

    /// <summary>
    /// How many times a throttled cell is retried before it is recorded as an error.
    /// </summary>
    /// <remarks>
    /// A ten thousand cell run against a DataZoneStandard deployment hits its per-minute token quota
    /// long before it hits anything else: a calibration run at concurrency ten lost twenty two
    /// percent of its cells to HTTP 429 with no retry. Backing off and retrying is what turns that
    /// into a slower run rather than a holed results file, and it costs nothing when quota is free.
    /// </remarks>
    private const int ThrottleRetries = 6;

    public async Task<ActionResponse> RunAsync(TextAction action, string selection, CancellationToken ct)
    {
        var userMessage = TextActionPrompt.BuildUserMessage(selection);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Every attempt takes a permit, retries included. Charging only first attempts would
                // let a throttled run's retries escape the budget, which is the moment the deployment
                // is least able to absorb them.
                await _limiter.WaitAsync(ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);

                var (text, usage) = await _clients[action.Id].SendAsync(userMessage, cts.Token).ConfigureAwait(false);
                var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                _limiter.ReportSuccess();

                return new ActionResponse(
                    text ?? string.Empty,
                    elapsed,
                    usage?.InputTokens,
                    usage?.OutputTokens,
                    usage?.ReasoningTokens,
                    Error: null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failure(started, $"timed out after {_timeout.TotalSeconds:F0}s");
            }
            catch (Exception ex) when (IsThrottled(ex) && attempt < ThrottleRetries)
            {
                // Tell the shared governor first. It halves the rate for EVERY worker, which is the
                // part that actually resolves the overload; this call's own backoff below only
                // decides when this one cell tries again.
                var retryAfter = RetryAfter(ex);
                _limiter.ReportThrottled(retryAfter);

                // Exponential backoff with jitter. Jitter matters here specifically: without it the
                // eight in-flight workers that were throttled together wake together and throttle
                // again on the same second.
                var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(started, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// True for a rate-limit response. Matched on the message because the OpenAI client surfaces the
    /// status inside <c>ClientResultException</c> rather than on a typed property.
    /// </summary>
    /// <summary>
    /// The service's own Retry-After, when it sent one.
    /// </summary>
    /// <remarks>
    /// Preferred over any computed backoff: Azure knows when the token window rolls over and this
    /// class does not. Both spellings are handled, delta-seconds and an HTTP date, because the header
    /// is defined as either. Anything unparseable falls back to the exponential schedule rather than
    /// throwing, since a failure to read a header must never fail the cell.
    /// </remarks>
    private static TimeSpan? RetryAfter(Exception ex)
    {
        if (ex is not System.ClientModel.ClientResultException result)
        {
            return null;
        }

        try
        {
            var response = result.GetRawResponse();
            if (response is null ||
                !response.Headers.TryGetValue("Retry-After", out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (int.TryParse(value, out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, 120));
            }

            if (DateTimeOffset.TryParse(value, out var when))
            {
                var delta = when - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(delta.TotalSeconds, 120)) : null;
            }
        }
        catch (Exception)
        {
            // A header we cannot read is not a reason to lose the cell.
        }

        return null;
    }

    private static bool IsThrottled(Exception ex) =>
        ex.Message.Contains("429", StringComparison.Ordinal) ||
        ex.Message.Contains("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase);

    private static ActionResponse Failure(long started, string error) => new(
        string.Empty,
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        null,
        null,
        null,
        error);

    /// <summary>
    /// Builds the credential, preferring a SUBSCRIPTION-scoped Azure CLI credential.
    /// </summary>
    /// <remarks>
    /// This is not a stylistic choice. On a machine signed in to several tenants,
    /// <c>az account get-access-token --tenant &lt;id&gt; --scope https://ai.azure.com/.default</c>
    /// returns "interaction required" for a tenant whose cached account is not the active one, while
    /// <c>--subscription &lt;id&gt;</c> succeeds, because a subscription selects the cached account as
    /// well as its tenant. <c>AzureCliCredentialOptions.Subscription</c> is the only way to ask
    /// Azure.Identity for that form, and <c>AzureCredentialFactory</c> in Scribe.Core makes the same
    /// choice for the same reason. Returning null falls back to the benchmark's own
    /// <c>DefaultAzureCredential</c> chain.
    /// </remarks>
    private static TokenCredential? BuildCredential(string? subscription, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(subscription))
        {
            return null;
        }

        // Subscription and tenant are deliberately not set together: Azure CLI treats them as
        // alternative ways to name the same account, and passing both is how the "interaction
        // required" failure gets reintroduced.
        var cli = new AzureCliCredential(new AzureCliCredentialOptions
        {
            Subscription = subscription.Trim(),
            ProcessTimeout = TimeSpan.FromSeconds(60),
        });

        // Serialized because the token expires roughly hourly and every in-flight cell then races to
        // refresh it, each shelling out to az against one shared CLI token cache. That contention is
        // what killed a run at 824 cells with a burst of AuthenticationFailedException. See
        // SerializedCliCredential for the full account.
        return new SerializedCliCredential(cli);
    }
}
