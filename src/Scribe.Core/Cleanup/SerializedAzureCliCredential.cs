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
        if (_testGate is null)
        {
            return await AzureCliProcessCoordinator.RunAsync(
                token => _inner.GetTokenAsync(requestContext, token),
                cancellationToken).ConfigureAwait(false);
        }

        await _testGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _testGate.Release();
        }
    }
}
