using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Scribe.Core.TextActions;
using Scribe.Evals.Benchmark;
using Scribe.StyleEval.Corpus;

namespace Scribe.StyleEval.Judge;

/// <summary>What one judge call produced.</summary>
internal sealed record JudgeResponse(
    JudgeVerdict? Verdict,
    long LatencyMs,
    long? InputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    string? Error);

/// <summary>
/// Sends one cell to the judge deployment and returns a schema-conformant verdict.
/// </summary>
/// <remarks>
/// <para>
/// One client per action, exactly as <c>StyleActionClient</c> does, because the judge's instructions
/// are fixed per action: the action's own text, its goal question and the rulebook it was given. It
/// also means the largest part of every request is identical across a whole action's worth of cells,
/// which is the shape prompt caching rewards.
/// </para>
/// <para>
/// The transport is the same <c>DirectResponsesCleanupClient</c> the generation half uses, with a
/// JSON schema attached. Reusing it rather than standing up a second Azure path is what keeps the
/// credential behaviour, the retry policy and the token accounting identical on both halves.
/// </para>
/// </remarks>
internal sealed class JudgeClient
{
    private readonly Dictionary<string, DirectResponsesCleanupClient> _clients = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;

    public JudgeClient(
        string endpoint,
        string deployment,
        string? subscription,
        string? tenantId,
        IEnumerable<TextAction> actions,
        ReasoningEffort? reasoning,
        int maxOutputTokens,
        TimeSpan timeout)
    {
        _timeout = timeout;
        Deployment = deployment;
        Endpoint = endpoint;

        var credential = BuildCredential(subscription);
        var schema = new JsonSchemaFormat(JudgeSchema.Name, JudgeSchema.Json, JudgeSchema.Description, Strict: true);

        foreach (var action in actions)
        {
            _clients[action.Id] = new DirectResponsesCleanupClient(
                endpoint,
                deployment,
                tenantId,
                JudgePrompt.BuildInstructions(action),
                reasoning,
                maxOutputTokens,
                timeout,
                disableRetries: false,
                credential,
                schema);
        }
    }

    /// <summary>The deployment producing verdicts. Recorded on every row.</summary>
    public string Deployment { get; }

    /// <summary>The account endpoint the judge runs against.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// How many times a throttled judge call is retried before it is recorded as an error. Same
    /// reasoning as the generation half: a quota response is a slower run, not a holed results file.
    /// </summary>
    private const int ThrottleRetries = 6;

    /// <summary>Judges one answer. Never throws for a service failure; returns it as an error.</summary>
    public async Task<JudgeResponse> JudgeAsync(
        Scenario scenario,
        TextAction action,
        string output,
        CancellationToken ct)
    {
        var userMessage = JudgePrompt.BuildUserMessage(scenario, action, output);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);

                var (text, usage) = await _clients[action.Id].SendAsync(userMessage, cts.Token).ConfigureAwait(false);
                var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var (verdict, error) = JudgeVerdictParser.Parse(text, scenario.Text, output);

                return new JudgeResponse(
                    verdict,
                    elapsed,
                    usage?.InputTokens,
                    usage?.OutputTokens,
                    usage?.ReasoningTokens,
                    error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failure(started, $"timed out after {_timeout.TotalSeconds:F0}s");
            }
            catch (Exception ex) when (IsThrottled(ex) && attempt < ThrottleRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)) +
                            TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(started, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One cheap call that proves the judge deployment answers and honours the schema, so a
    /// misconfigured judge fails in seconds rather than after a thousand cells of errors.
    /// </summary>
    public async Task<string?> ValidateAsync(TextAction action, CancellationToken ct)
    {
        var scenario = new Scenario
        {
            Id = "validation-000",
            Category = "validation",
            Text = "The build is blocked until the signing cert is replaced. It expires on 12 September.",
            Note = "Connectivity and schema check only.",
        };

        var response = await JudgeAsync(
            scenario,
            action,
            "The build is blocked until the signing cert is replaced. It expires on 12 September.",
            ct).ConfigureAwait(false);

        return response.Error;
    }

    private static bool IsThrottled(Exception ex) =>
        ex.Message.Contains("429", StringComparison.Ordinal) ||
        ex.Message.Contains("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase);

    private static JudgeResponse Failure(long started, string error) => new(
        null,
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        null,
        null,
        null,
        error);

    /// <summary>
    /// The same subscription-scoped Azure CLI credential the generation half uses. On a machine
    /// signed in to several tenants it is the only form that mints an ai.azure.com token for a
    /// tenant whose cached account is not the active one.
    /// </summary>
    private static TokenCredential? BuildCredential(string? subscription) =>
        string.IsNullOrWhiteSpace(subscription)
            ? null
            : new AzureCliCredential(new AzureCliCredentialOptions
            {
                Subscription = subscription.Trim(),
                ProcessTimeout = TimeSpan.FromSeconds(60),
            });
}
