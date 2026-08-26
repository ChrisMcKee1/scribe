using Azure.Core;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Wraps a token credential so only one token acquisition runs at a time, and pre-warms the token
/// before any parallel work starts.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a 10,000 cell run reliably dies partway without it. The failure looks like
/// <c>AuthenticationFailedException: Azure CLI authentication failed due to an unknown error</c>
/// arriving on several cells at once, roughly an hour into a run, and it is not random: an Azure CLI
/// token lives about an hour, so when it expires every in-flight request hits the refresh at the same
/// moment and each one shells out to <c>az</c>. The CLI shares a single token cache across processes,
/// and concurrent invocations contend on it. On a machine signed in to several tenants that
/// contention turns into an outright failure rather than a wait.
/// </para>
/// <para>
/// AGENTS.md records this exact problem in the product: "Azure CLI token requests are serialized
/// through AzureCliProcessCoordinator: az shares one token cache, and concurrent processes made it
/// time out on multi-tenant machines." Scribe.Core solves it with an internal coordinator this tool
/// cannot reach, so the same idea is reimplemented here in the smallest form that works.
/// </para>
/// <para>
/// The semaphore is the whole mechanism. Azure.Identity already caches a valid token per credential
/// instance and returns it without touching the CLI, so serializing costs nothing on the common path;
/// it only turns the once-an-hour refresh stampede into one refresh that the rest then reuse.
/// </para>
/// </remarks>
internal sealed class SerializedCliCredential(TokenCredential inner) : TokenCredential, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        try
        {
            return inner.GetToken(requestContext, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Acquires a token once before the run starts, so the first wave of parallel calls finds a warm
    /// cache instead of racing to fill an empty one.
    /// </summary>
    /// <remarks>
    /// Also turns a broken sign-in into an immediate, readable failure at startup rather than a
    /// hundred identical authentication errors scattered through the results file an hour later.
    /// </remarks>
    public async Task WarmAsync(string scope, CancellationToken cancellationToken)
    {
        var context = new TokenRequestContext([scope]);
        _ = await GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
