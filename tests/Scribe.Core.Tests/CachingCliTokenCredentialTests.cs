using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

#pragma warning disable OPENAI001

namespace Scribe.Core.Tests;

/// <summary>
/// PerfFlags.CliAccessTokenCache (PLAT-O-01 with PLAT-A-02): an in-memory access-token cache for Azure CLI sign-in only.
/// Every credential here is synthetic: no test runs az, reaches Entra, or asks a credential the factory built for a token.
/// </summary>
/// <remarks>
/// The tests that touch the factory's static state (its cached instances and its invalidation version) all live in this
/// class, which xUnit runs one test at a time, and no other test calls <see cref="AzureCredentialInvalidation.Invalidate"/>.
/// </remarks>
public sealed class CachingCliTokenCredentialTests
{
    private static readonly string[] AiScope = [AzureOpenAIResponsesClientFactory.AzureAIScope];
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // The full constructor, positionally, so no optional-parameter overload is ambiguous.
    private static TokenRequestContext Context(
        string[]? scopes = null, string? tenant = null, bool cae = false, string? claims = null, bool proofOfPossession = false) =>
        new(
            scopes ?? AiScope,
            null,
            claims,
            tenant,
            cae,
            proofOfPossession,
            proofOfPossession ? "synthetic-nonce" : null,
            proofOfPossession ? new Uri("https://wire-test.example.invalid/openai/v1/responses") : null,
            proofOfPossession ? "POST" : null);

    private static AccessToken Token(string value, int expiresInMinutes = 60, int? refreshInMinutes = null) =>
        new(value, Start.AddMinutes(expiresInMinutes), refreshInMinutes is { } refresh ? Start.AddMinutes(refresh) : null);

    [Fact]
    public async Task Off_every_request_asks_the_cli_and_on_one_token_serves_every_request()
    {
        var clock = new Clock(Start);

        // Off: the credential the serving client holds today. System.ClientModel's BearerTokenPolicy asks it on every request.
        var uncached = new FakeCli(clock);
        var sentOff = await SendThroughResponsesClientAsync(uncached, requests: 5);
        Assert.Equal(5, uncached.Calls);
        Assert.Equal(new[] { "Bearer account-a-1", "Bearer account-a-2", "Bearer account-a-3", "Bearer account-a-4", "Bearer account-a-5" }, sentOff);

        // On: the same five requests, one az call.
        var inner = new FakeCli(clock);
        var sentOn = await SendThroughResponsesClientAsync(new CachingCliTokenCredential(inner, clock, () => 0), requests: 5);
        Assert.Equal(1, inner.Calls);
        Assert.All(sentOn, header => Assert.Equal("Bearer account-a-1", header));
        Assert.Equal(5, sentOn.Count);
    }

    [Fact]
    public async Task A_hundred_concurrent_requests_share_one_acquisition_and_a_hit_completes_synchronously()
    {
        var clock = new Clock(Start);
        var held = new HeldCalls();
        var inner = new FakeCli(clock) { Script = held.Script };
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);

        var waiters = new Task<AccessToken>[100];
        Parallel.For(0, waiters.Length, i => waiters[i] = credential.GetTokenAsync(Context(), CancellationToken.None).AsTask());

        Assert.Equal(1, inner.Calls);
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        held.Call(1).SetResult(Token("shared"));
        var tokens = await Task.WhenAll(waiters);
        Assert.All(tokens, token => Assert.Equal("shared", token.Token));

        var hit = credential.GetTokenAsync(Context(), CancellationToken.None);
        Assert.True(hit.IsCompletedSuccessfully);
        Assert.Equal("shared", (await hit).Token);
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(30, 60, 30)]   // a refresh hint earlier than the margin: refreshed at the hint
    [InlineData(null, 60, 55)] // no hint: five minutes before expiry
    [InlineData(120, 60, 55)]  // a hint later than expiry: expiry is the ceiling, so still five minutes before it
    [InlineData(58, 60, 55)]   // a hint inside the margin: the margin comes first
    public async Task A_cached_token_is_served_until_the_earlier_of_its_hint_and_five_minutes_before_expiry(
        int? refreshInMinutes, int expiresInMinutes, int refreshedAtMinute)
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock) { Issue = call => Token($"token-{call}", expiresInMinutes, refreshInMinutes) };
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        await credential.GetTokenAsync(Context(), default);

        clock.Now = Start.AddMinutes(refreshedAtMinute).AddTicks(-1);
        Assert.Equal("token-1", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal("token-1", credential.GetToken(Context(), default).Token);
        Assert.Equal(1, inner.Calls);

        clock.Now = Start.AddMinutes(refreshedAtMinute);
        Assert.Equal("token-2", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData("five minutes left")]
    [InlineData("under five minutes left")]
    [InlineData("expired")]
    [InlineData("refresh hint already past")]
    [InlineData("expiry at DateTimeOffset.MinValue")]
    public async Task A_token_that_is_not_fresh_reaches_its_callers_but_is_never_served_from_the_cache(string shape)
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock)
        {
            Issue = call => shape switch
            {
                "five minutes left" => new AccessToken($"token-{call}", Start + CachingCliTokenCredential.RefreshMargin),
                "under five minutes left" => new AccessToken($"token-{call}", Start.AddMinutes(4)),
                "expired" => new AccessToken($"token-{call}", Start.AddMinutes(-1)),
                "refresh hint already past" => new AccessToken($"token-{call}", Start.AddHours(1), Start.AddSeconds(-1)),
                _ => new AccessToken($"token-{call}", DateTimeOffset.MinValue),
            },
        };
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);

        Assert.Equal("token-1", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal("token-2", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal("token-3", credential.GetToken(Context(), default).Token);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Scopes_tenants_and_cae_never_share_an_entry()
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        TokenRequestContext[] distinct =
        [
            Context(),
            Context(scopes: ["https://cognitiveservices.azure.com/.default"]),
            Context(tenant: "tenant-one"),
            Context(tenant: "tenant-two"),
            Context(cae: true),
            Context(tenant: "tenant-one", cae: true),
            Context(scopes: ["scope-a", "scope-b"]),
            Context(scopes: ["scope-b", "scope-a"]),
        ];

        var first = new List<string>();
        foreach (var context in distinct)
        {
            first.Add((await credential.GetTokenAsync(context, default)).Token);
        }

        Assert.Equal(distinct.Length, inner.Calls);
        Assert.Equal(distinct.Length, first.Distinct(StringComparer.Ordinal).Count());

        // Asked again, each context gets its own token back and nothing new is acquired.
        for (var i = 0; i < distinct.Length; i++)
        {
            Assert.Equal(first[i], (await credential.GetTokenAsync(distinct[i], default)).Token);
            Assert.Equal(first[i], credential.GetToken(distinct[i], default).Token);
        }

        Assert.Equal(distinct.Length, inner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claims_and_proof_of_possession_bypass_the_cache(bool proofOfPossession)
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        var bypassing = proofOfPossession
            ? Context(proofOfPossession: true)
            : Context(claims: "{\"access_token\":{\"nbf\":{\"essential\":true,\"value\":\"1700000000\"}}}");

        Assert.Equal("account-a-1", (await credential.GetTokenAsync(bypassing, default)).Token);
        Assert.Equal("account-a-2", (await credential.GetTokenAsync(bypassing, default)).Token);
        Assert.Equal("account-a-3", credential.GetToken(bypassing, default).Token);
        Assert.Equal(3, inner.Calls);
        Assert.All(inner.Requests, request => Assert.Equal(proofOfPossession, request.Context.IsProofOfPossessionEnabled));

        // Nothing a bypassing request received was kept for a plain one.
        Assert.Equal("account-a-4", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task An_invalidation_reaches_a_client_that_already_holds_the_instance()
    {
        var clock = new Clock(Start);
        var version = 0;
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => Volatile.Read(ref version));

        Assert.Equal("account-a-1", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal("account-a-1", (await credential.GetTokenAsync(Context(), default)).Token);

        // Settings changed the identity, or a Save that failed invalidated it anyway: the held client asks az again on its
        // next request, and a failed Save's invalidation costs one az call and keeps the identity az reports.
        Interlocked.Increment(ref version);
        Assert.Equal("account-a-2", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal("account-a-2", (await credential.GetTokenAsync(Context(), default)).Token);

        Interlocked.Increment(ref version);
        Assert.Equal("account-a-3", credential.GetToken(Context(), default).Token);
        Assert.Equal("account-a-3", credential.GetToken(Context(), default).Token);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task An_acquisition_that_crosses_an_invalidation_answers_its_callers_but_is_never_stored()
    {
        var clock = new Clock(Start);
        var version = 0;
        var held = new HeldCalls();
        var inner = new FakeCli(clock) { Script = held.Script };
        var credential = new CachingCliTokenCredential(inner, clock, () => Volatile.Read(ref version));

        var before = credential.GetTokenAsync(Context(), default).AsTask();
        Interlocked.Increment(ref version);
        var after = credential.GetTokenAsync(Context(), default).AsTask();

        // The request after the change did not join the one before it, and the change cancelled nothing.
        Assert.Equal(2, inner.Calls);
        Assert.False(inner.RequestAt(1).Token.IsCancellationRequested);

        held.Call(1).SetResult(Token("before-the-change"));
        Assert.Equal("before-the-change", (await before).Token);

        // Its answer is not the current entry: a new request joins the acquisition made after the change.
        var joined = credential.GetTokenAsync(Context(), default).AsTask();
        Assert.False(joined.IsCompleted);
        Assert.Equal(2, inner.Calls);

        held.Call(2).SetResult(Token("after-the-change"));
        Assert.Equal("after-the-change", (await after).Token);
        Assert.Equal("after-the-change", (await joined).Token);
        Assert.Equal("after-the-change", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_cancelled_waiter_ends_only_its_own_wait()
    {
        var clock = new Clock(Start);
        var held = new HeldCalls();
        var inner = new FakeCli(clock) { Script = (call, token) => held.Call(call).Task.WaitAsync(token) };
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        using var cancelFirst = new CancellationTokenSource();

        var first = credential.GetTokenAsync(Context(), cancelFirst.Token).AsTask();
        var second = credential.GetTokenAsync(Context(), CancellationToken.None).AsTask();
        Assert.Equal(1, inner.Calls);

        await cancelFirst.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        Assert.False(inner.RequestAt(1).Token.IsCancellationRequested);

        held.Call(1).SetResult(Token("shared"));
        Assert.Equal("shared", (await second).Token);
        Assert.Equal("shared", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(true)]  // az stops when it is cancelled
    [InlineData(false)] // az finishes anyway and returns a token no one is waiting for any more
    public async Task When_the_last_waiter_leaves_the_acquisition_is_cancelled_and_stores_nothing(bool cliStopsWhenCancelled)
    {
        var clock = new Clock(Start);
        var held = new HeldCalls(inlineContinuations: true);
        var inner = new FakeCli(clock)
        {
            Script = cliStopsWhenCancelled ? (call, token) => held.Call(call).Task.WaitAsync(token) : held.Script,
        };
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        using var cancel = new CancellationTokenSource();

        // Shutdown, or a dictation cancelled while it waited for its token: its one waiter leaves.
        var only = credential.GetTokenAsync(Context(), cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        Assert.True(inner.RequestAt(1).Token.IsCancellationRequested);

        if (!cliStopsWhenCancelled)
        {
            // Inline continuations: the abandoned acquisition has finished by the time this returns.
            held.Call(1).SetResult(Token("nobody-asked"));
        }

        // A later request is never served the abandoned acquisition's token: it starts its own.
        held.Call(2).SetResult(Token("fresh"));
        Assert.Equal("fresh", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal("fresh", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_failure_reaches_every_waiter_and_caches_nothing()
    {
        var clock = new Clock(Start);
        var held = new HeldCalls();
        var inner = new FakeCli(clock) { Script = held.Script };
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);

        var first = credential.GetTokenAsync(Context(), default).AsTask();
        var second = credential.GetTokenAsync(Context(), default).AsTask();
        var failure = new AuthenticationFailedException("synthetic: not signed in");
        held.Call(1).SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<AuthenticationFailedException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<AuthenticationFailedException>(() => second));

        held.Call(2).SetResult(Token("after-the-failure"));
        Assert.Equal("after-the-failure", (await credential.GetTokenAsync(Context(), default)).Token);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_request_already_cancelled_fails_at_once_on_a_miss_or_a_hit()
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await credential.GetTokenAsync(Context(), cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => credential.GetToken(Context(), cancelled.Token));
        Assert.Equal(0, inner.Calls);

        await credential.GetTokenAsync(Context(), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await credential.GetTokenAsync(Context(), cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => credential.GetToken(Context(), cancelled.Token));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Two_accounts_of_one_tenant_are_told_apart_at_the_next_refresh_or_invalidation_not_the_next_request()
    {
        var clock = new Clock(Start);
        var version = 0;
        var inner = new FakeCli(clock);
        var cached = new CachingCliTokenCredential(inner, clock, () => Volatile.Read(ref version));
        var context = Context(tenant: "one-tenant");

        Assert.Equal("account-a-1", (await cached.GetTokenAsync(context, default)).Token);

        // az login as another account of the same tenant and subscription, outside Scribe. The settings did not change,
        // so nothing in the request can tell; only the cache decides when the new account is seen.
        inner.Account = "account-b";
        Assert.Equal("account-b-2", (await inner.GetTokenAsync(context, default)).Token);  // without the cache: the next request
        Assert.Equal("account-a-1", (await cached.GetTokenAsync(context, default)).Token); // with it: not yet

        clock.Now = Start.AddMinutes(55);
        Assert.Equal("account-b-3", (await cached.GetTokenAsync(context, default)).Token); // at the refresh

        // A change made through Scribe (Settings, then AzureCredentialInvalidation) is seen at the next request.
        inner.Account = "account-c";
        Interlocked.Increment(ref version);
        Assert.Equal("account-c-4", (await cached.GetTokenAsync(context, default)).Token);
    }

    [Fact]
    public async Task A_hit_allocates_nothing()
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        var context = Context(tenant: "tenant-one");
        var token = (await credential.GetTokenAsync(context, default)).Token;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(token, (await credential.GetTokenAsync(context, default)).Token);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var hit = credential.GetTokenAsync(context, default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(hit.IsCompletedSuccessfully);
        Assert.Equal(token, (await hit).Token);
        Assert.Equal(0, allocated);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_hit_never_waits_for_the_azure_cli_gate_and_a_miss_still_does()
    {
        var clock = new Clock(Start);
        var gate = new SemaphoreSlim(1, 1);
        var az = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(new SerializedAzureCliCredential(az, gate), clock, () => 0);
        await credential.GetTokenAsync(Context(), default);

        // Settings holds the gate while az login or az account list runs.
        await gate.WaitAsync();
        Task<AccessToken> miss;
        try
        {
            var hit = credential.GetTokenAsync(Context(), default);
            Assert.True(hit.IsCompletedSuccessfully);
            Assert.Equal("account-a-1", (await hit).Token);

            miss = credential.GetTokenAsync(Context(tenant: "another-tenant"), default).AsTask();
            Assert.False(miss.IsCompleted);
            Assert.Equal(1, az.Calls);
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal("account-a-2", (await miss).Token);
        Assert.Equal(2, az.Calls);
    }

    [Fact]
    public async Task A_caller_that_changes_its_scopes_array_afterwards_cannot_change_an_entry()
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        var scopes = new[] { "scope-a" };

        var first = (await credential.GetTokenAsync(Context(scopes: scopes), default)).Token;
        scopes[0] = "scope-b";
        var second = (await credential.GetTokenAsync(Context(scopes: scopes), default)).Token;

        Assert.NotEqual(first, second);
        Assert.Equal(first, (await credential.GetTokenAsync(Context(scopes: ["scope-a"]), default)).Token);
        Assert.Equal(second, (await credential.GetTokenAsync(Context(scopes: ["scope-b"]), default)).Token);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Nothing_refreshes_on_its_own()
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock, () => 0);
        await credential.GetTokenAsync(Context(), default);

        // Past the refresh time and past expiry, with no request: no az call, and (Clock.CreateTimer throws) no timer.
        clock.Now = Start.AddHours(3);
        await Task.Yield();
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void Only_azure_cli_sign_in_under_the_flag_gets_the_cache()
    {
        var cli = AzureCredentialRequest.Cli("synthetic-tenant", "00000000-0000-0000-0000-00000000000a");
        Assert.IsType<CachingCliTokenCredential>(AzureCredentialFactory.Create(cli, cacheCliTokens: true));
        Assert.IsType<SerializedAzureCliCredential>(AzureCredentialFactory.Create(cli, cacheCliTokens: false));
        Assert.IsType<SerializedAzureCliCredential>(AzureCredentialFactory.Create(cli));

        var servicePrincipal = new AzureCredentialRequest(
            AzureAuthMode.ServicePrincipal,
            "00000000-0000-0000-0000-000000000001",
            null,
            "00000000-0000-0000-0000-000000000002",
            "not-a-real-secret");
        Assert.IsType<ClientSecretCredential>(AzureCredentialFactory.Create(servicePrincipal, cacheCliTokens: true));
        Assert.IsType<ClientSecretCredential>(AzureCredentialFactory.Create(servicePrincipal));
    }

    [Fact]
    public void The_factory_keeps_one_cached_instance_per_settings_and_an_invalidation_drops_it()
    {
        var cli = AzureCredentialRequest.Cli("synthetic-tenant", "00000000-0000-0000-0000-00000000000b");
        var first = AzureCredentialFactory.Create(cli, cacheCliTokens: true);

        // One instance, so one cache, for the same settings whichever account az is signed in to.
        Assert.Same(first, AzureCredentialFactory.Create(cli, cacheCliTokens: true));
        Assert.Same(first, AzureCredentialFactory.Create(cli with { SubscriptionId = "  00000000-0000-0000-0000-00000000000b " }, cacheCliTokens: true));

        var before = AzureCredentialFactory.InvalidationVersion;
        AzureCredentialInvalidation.Invalidate();
        Assert.True(AzureCredentialFactory.InvalidationVersion > before);
        Assert.NotSame(first, AzureCredentialFactory.Create(cli, cacheCliTokens: true));
    }

    [Fact]
    public async Task A_held_instance_follows_the_factory_s_own_invalidation()
    {
        var clock = new Clock(Start);
        var inner = new FakeCli(clock);
        var credential = new CachingCliTokenCredential(inner, clock);

        await credential.GetTokenAsync(Context(), default);
        await credential.GetTokenAsync(Context(), default);
        Assert.Equal(1, inner.Calls);

        AzureCredentialInvalidation.Invalidate();
        await credential.GetTokenAsync(Context(), default);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Only_the_two_serving_clients_ask_for_the_cache()
    {
        var service = ReadSource("src", "Scribe.Core", "Cleanup", "TextCleanupService.cs");
        Assert.Contains(
            "private bool CachesCliTokens => _perfFlags.IsOn(Diagnostics.PerfFlags.CliAccessTokenCache);", service, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(service, @"options\.AzureClientSecret\),\s*CachesCliTokens\)").Count);

        // Test connection keeps the uncached credential, so it always asks az, as before.
        var test = MethodText(service, "private static Azure.Core.TokenCredential CreateTestCredential(");
        Assert.Contains("AzureCredentialFactory.Create(request)", test, StringComparison.Ordinal);
        Assert.DoesNotContain("CachesCliTokens", test, StringComparison.Ordinal);
        Assert.DoesNotContain("cacheCliTokens", test, StringComparison.Ordinal);

        // The cache never decides what a request may send: admission is judged at the hand-off, after the token.
        var cache = ReadSource("src", "Scribe.Core", "Cleanup", "CachingCliTokenCredential.cs");
        Assert.DoesNotContain("CleanupAdmission", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("TryHandOff", cache, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("cacheCliTokens: true", text, StringComparison.Ordinal);
            if (name is not ("TextCleanupService.cs" or "AzureCredentialFactory.cs"))
            {
                Assert.False(text.Contains("CachesCliTokens", StringComparison.Ordinal), $"{name} asks for the token cache.");
                Assert.False(text.Contains("cacheCliTokens", StringComparison.Ordinal), $"{name} asks for the token cache.");
            }

            if (name is not ("AzureCredentialFactory.cs" or "CachingCliTokenCredential.cs"))
            {
                Assert.False(text.Contains("new CachingCliTokenCredential(", StringComparison.Ordinal), $"{name} builds the token cache.");
            }
        }
    }

    private static async Task<List<string>> SendThroughResponsesClientAsync(TokenCredential credential, int requests)
    {
        var authorizations = new List<string>();
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            lock (authorizations)
            {
                authorizations.Add(request.Headers.Authorization?.ToString() ?? "(none)");
            }

            return Task.FromResult(ResponsesAnswer("Hello."));
        });

        var client = AzureOpenAIResponsesClientFactory.CreateClientWithTokenCredential(
            new Uri("https://wire-test.example.invalid/"),
            credential,
            configure: ScriptedHttpHandler.Install(handler));
        var agent = TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "gpt-6-astra", "Clean up the text.");
        for (var i = 0; i < requests; i++)
        {
            var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"));
            Assert.Equal("Hello.", answer.Text);
        }

        return authorizations;
    }

    private static HttpResponseMessage ResponsesAnswer(string text) => ScriptedHttpHandler.Json(
        System.Net.HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    private static string MethodText(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Not found: {signature}");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"No end for: {signature}");
        return source[start..end];
    }

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // A clock the test sets. The cache must never create a timer, so asking for one fails the test.
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("The access-token cache must not create a timer.");
    }

    // Stands in for the serialized Azure CLI credential: each call is one az process. By default it answers at once with
    // a token that lives an hour from the clock's time, named for the account az is signed in to and the call number.
    private sealed class FakeCli : TokenCredential
    {
        private readonly Clock _clock;
        private int _calls;

        public FakeCli(Clock clock)
        {
            _clock = clock;
            Issue = call => new AccessToken($"{Account}-{call}", _clock.Now.AddHours(1));
        }

        public int Calls => Volatile.Read(ref _calls);

        public string Account { get; set; } = "account-a";

        public Func<int, AccessToken> Issue { get; set; }

        public Func<int, CancellationToken, Task<AccessToken>>? Script { get; set; }

        public ConcurrentQueue<(int Call, TokenRequestContext Context, CancellationToken Token)> Requests { get; } = new();

        public (TokenRequestContext Context, CancellationToken Token) RequestAt(int call)
        {
            var request = Requests.Single(entry => entry.Call == call);
            return (request.Context, request.Token);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            Requests.Enqueue((call, requestContext, cancellationToken));
            return Script is { } script ? new ValueTask<AccessToken>(script(call, cancellationToken)) : ValueTask.FromResult(Issue(call));
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    // Each az call waits for the test to answer it.
    private sealed class HeldCalls(bool inlineContinuations = false)
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<AccessToken>> _calls = new();

        public TaskCompletionSource<AccessToken> Call(int call) => _calls.GetOrAdd(
            call,
            _ => new TaskCompletionSource<AccessToken>(
                inlineContinuations ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously));

        public Func<int, CancellationToken, Task<AccessToken>> Script => (call, _) => Call(call).Task;
    }
}

#pragma warning restore OPENAI001