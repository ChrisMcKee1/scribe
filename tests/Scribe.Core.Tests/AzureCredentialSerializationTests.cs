using Azure.Core;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class AzureCredentialSerializationTests
{
    [Fact]
    public async Task Shared_gate_prevents_concurrent_token_processes()
    {
        var inner = new TrackingCredential();
        using var gate = new SemaphoreSlim(1, 1);
        var first = new SerializedAzureCliCredential(inner, gate);
        var second = new SerializedAzureCliCredential(inner, gate);
        var request = new TokenRequestContext(["https://management.azure.com/.default"]);

        await Task.WhenAll(
            first.GetTokenAsync(request, CancellationToken.None).AsTask(),
            second.GetTokenAsync(request, CancellationToken.None).AsTask());

        Assert.Equal(1, inner.MaxConcurrency);
    }

    private sealed class TrackingCredential : TokenCredential
    {
        private int _active;
        private int _maxConcurrency;

        public int MaxConcurrency => _maxConcurrency;

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maxConcurrency, active);
            try
            {
                await Task.Delay(25, cancellationToken);
                return new AccessToken("token", DateTimeOffset.UtcNow.AddMinutes(5));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (current >= value)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref location, value, current) != current);
        }
    }
}
