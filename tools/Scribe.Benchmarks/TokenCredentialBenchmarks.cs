using Azure.Core;
using BenchmarkDotNet.Attributes;
using Scribe.Core.Cleanup;

namespace Scribe.Benchmarks;

/// <summary>
/// D2 (PLAT-O-01 with PLAT-A-02): the credential call every cleanup request makes. Today's path is the serialized Azure CLI
/// credential through the process-wide Azure CLI gate, over a fake az that answers at once, so its numbers are the
/// in-process overhead only: the az process itself is what the access-token cache removes (on by default since 0.5.2;
/// <see cref="Scribe.Core.Diagnostics.PerfFlags.CliTokenEveryRequest"/> brings back the old path)
/// (752 to 802 ms warm for the az chain against an empty profile in round 1's O-M2; the signed-in path was not
/// measured, and no benchmark may run az). The cached path is a hit within the token's life, with its own gate held by
/// another caller for the whole run: a hit never waits for it.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Cleanup")]
public class TokenCredentialBenchmarks
{
    private static readonly TokenRequestContext Context = new([AzureOpenAIResponsesClientFactory.AzureAIScope]);

    private readonly SemaphoreSlim _heldGate = new(1, 1);
    private TokenCredential _before = null!;
    private TokenCredential _today = null!;
    private CachingCliTokenCredential _cached = null!;

    [GlobalSetup]
    public void Setup()
    {
        _before = new BaselineSerializedCli(new InstantCli());
        _today = new SerializedAzureCliCredential(new InstantCli());

        _cached = new CachingCliTokenCredential(new SerializedAzureCliCredential(new InstantCli(), _heldGate));
        var first = _cached.GetTokenAsync(Context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (first.Token != InstantCli.Value)
        {
            throw new InvalidOperationException("The cache did not take its first token.");
        }

        // Held until cleanup: any request that missed the cache would wait here for ever, so a finished run proves every
        // measured call was a hit.
        _heldGate.Wait();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _heldGate.Release();
        _heldGate.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<AccessToken> Before_77b22af_every_request_asks_the_serialized_cli_credential() =>
        _before.GetTokenAsync(Context, CancellationToken.None);

    // The same path with PLAT-O-03's gate and call timing, which every request pays with the flags off.
    [Benchmark]
    public ValueTask<AccessToken> After_flag_off_every_request_asks_the_serialized_cli_credential() =>
        _today.GetTokenAsync(Context, CancellationToken.None);

    [Benchmark]
    public ValueTask<AccessToken> CliAccessTokenCache_hit_with_the_cli_gate_held() =>
        _cached.GetTokenAsync(Context, CancellationToken.None);

    // 77b22af's SerializedAzureCliCredential.GetTokenAsync without a test gate, verbatim: the before of the row-2 timing.
    private sealed class BaselineSerializedCli(TokenCredential inner) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            await AzureCliProcessCoordinator.RunAsync(
                token => inner.GetTokenAsync(requestContext, token),
                cancellationToken).ConfigureAwait(false);
    }

    // Stands in for az: answers at once with a token that outlives the run.
    private sealed class InstantCli : TokenCredential
    {
        public const string Value = "benchmark-token";

        private readonly AccessToken _token = new(Value, DateTimeOffset.UtcNow.AddHours(2));

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => _token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_token);
    }
}