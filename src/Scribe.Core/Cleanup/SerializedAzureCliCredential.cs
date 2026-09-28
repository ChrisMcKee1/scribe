using System.Diagnostics;
using Azure.Core;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Azure CLI reads and refreshes one shared token cache. Serializing token requests prevents startup
/// cleanup validation and settings discovery from launching competing CLI processes against that
/// cache, which otherwise causes intermittent process timeouts on multi-tenant developer machines.
/// </summary>
internal sealed class SerializedAzureCliCredential : TokenCredential
{
    private readonly TokenCredential _inner;
    private readonly SemaphoreSlim? _testGate;

    internal SerializedAzureCliCredential(TokenCredential inner, SemaphoreSlim? gate = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _testGate = gate;
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (_testGate is null)
        {
            return AzureCliProcessCoordinator.Run(
                () => _inner.GetToken(requestContext, cancellationToken),
                cancellationToken);
        }

        _testGate.Wait(cancellationToken);
        try
        {
            return _inner.GetToken(requestContext, cancellationToken);
        }
        finally
        {
            _testGate.Release();
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        // Numbers only, for the cleanup's log line: how long this request waited for the one Azure CLI gate (Settings'
        // sign-in and account listing hold it too), recorded when the wait ended, by admission or before it, then how long
        // the CLI took, recorded when the call ended, with a token or not. Nothing about the wait or the call changes.
        var timings = CleanupAdmission.Current?.Timings;
        var requested = Stopwatch.GetTimestamp();
        var admitted = false;

        async ValueTask<AccessToken> AcquireAsync(CancellationToken token)
        {
            admitted = true;
            var called = Stopwatch.GetTimestamp();
            timings?.AddGateWait(Stopwatch.GetElapsedTime(requested, called), admitted: true);
            var finished = false;
            try
            {
                var accessToken = await _inner.GetTokenAsync(requestContext, token).ConfigureAwait(false);
                finished = true;
                return accessToken;
            }
            finally
            {
                timings?.AddTokenCall(Stopwatch.GetElapsedTime(called), finished);
            }
        }

        try
        {
            return _testGate is null
                ? await AzureCliProcessCoordinator.RunAsync(AcquireAsync, cancellationToken).ConfigureAwait(false)
                : await RunOnTestGateAsync(AcquireAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!admitted)
        {
            // The wait ended before admission (its cancellation): the time went to the queue, and no call began.
            timings?.AddGateWait(Stopwatch.GetElapsedTime(requested), admitted: false);
            throw;
        }
    }

    private async ValueTask<AccessToken> RunOnTestGateAsync(
        Func<CancellationToken, ValueTask<AccessToken>> acquire, CancellationToken cancellationToken)
    {
        await _testGate!.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await acquire(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _testGate.Release();
        }
    }
}
