using Azure.Core;

namespace Scribe.Core.Cleanup;

/// <summary>
/// PerfFlags.CliAccessTokenCache: an in-memory access-token cache for Azure CLI sign-in only, around the serialized CLI
/// credential. Without it every cleanup request, retries included, runs <c>az account get-access-token</c>, because the
/// cleanup clients' System.ClientModel <c>BearerTokenPolicy</c> asks the credential on every request and
/// <c>AzureCliCredential</c> caches nothing. The service principal keeps MSAL's own cache and never comes here, and
/// neither do Test connection and Settings discovery, which keep the uncached instance.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A token is served from the cache only while it is fresh: before its <c>RefreshOn</c> hint, if it has one, and
/// while more than five minutes remain before its <c>ExpiresOn</c>. Expiry is a ceiling whatever the hint says, so a hint
/// later than expiry changes nothing, and a token with five minutes or less to live, or already expired, reaches the
/// callers who asked for it but is never served from the cache.</item>
/// <item>Entries are keyed by the scopes in order, the tenant and the CAE flag; a request with claims or proof of
/// possession bypasses the cache entirely. Nothing is shared across scopes or tenants. The key cannot tell two accounts
/// of one tenant or subscription apart, because the settings cannot either: an account change made outside Scribe
/// (<c>az login</c> as someone else, <c>az logout</c>) is seen at the next refresh instead of the next request. That
/// window is what the maintainer approves with the flag.</item>
/// <item>One acquisition per key at a time (single flight), through the inner credential and so through the Azure CLI
/// gate. A waiter's cancellation ends only its own wait; the acquisition is cancelled when its last waiter leaves, and an
/// acquisition abandoned that way stores nothing, whatever it returns. A failure reaches every waiter still waiting and
/// caches nothing. A request already cancelled fails at once, on a hit too, as the CLI gate refused it before.</item>
/// <item>Identity changes made through Scribe: every call reads the invalidation version under the gate
/// (<see cref="AzureCredentialFactory.Invalidate"/> bumps it), so a client that already holds this instance acquires again
/// on its next request, and every entry is stamped with the version it was acquired under, so a stale one is never served.
/// An acquisition in flight when the version moves is not cancelled: it still answers the callers admitted before the
/// change, exactly as their <c>az</c> call answered them before the cache, but it is never stored, and requests after the
/// change never join it.</item>
/// <item>No timer, no background refresh, nothing on disk. Disabling cleanup, a failed Save or shutdown cancels nothing
/// the cleanup's own contract does not already cancel; what can outlive them is an acquisition some admitted caller is
/// still waiting for, and a token kept in memory until the next request finds it stale or the credential is dropped.</item>
/// </list>
/// </remarks>
internal sealed class CachingCliTokenCredential : TokenCredential
{
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _inner;
    private readonly TimeProvider _time;
    private readonly Func<int> _invalidationVersion;
    private readonly Action<TimeSpan, bool>? _sharedWait;
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Dictionary<Key, Acquisition> _inFlight = [];

    /// <param name="inner">The serialized Azure CLI credential.</param>
    /// <param name="time">The clock tokens are judged by.</param>
    /// <param name="invalidationVersion">The factory's invalidation version, or a test's.</param>
    /// <param name="sharedWait">
    /// Diagnostics only: told how long a request waited for an acquisition it did not start, or left one it started before
    /// that ended, and whether the token came (the inner credential's own numbers cover everything else). Never throws
    /// into a request: a failure here is ignored.
    /// </param>
    public CachingCliTokenCredential(
        TokenCredential inner,
        TimeProvider? time = null,
        Func<int>? invalidationVersion = null,
        Action<TimeSpan, bool>? sharedWait = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _time = time ?? TimeProvider.System;
        _invalidationVersion = invalidationVersion ?? (() => AzureCredentialFactory.InvalidationVersion);
        _sharedWait = sharedWait;
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (Bypasses(requestContext))
        {
            return _inner.GetTokenAsync(requestContext, cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<AccessToken>(cancellationToken);
        }

        var key = Key.Of(requestContext);
        Acquisition? acquisition;
        var started = false;
        lock (_gate)
        {
            // Read under the gate, so a later caller never sees an acquisition newer than its own version.
            var version = _invalidationVersion();
            if (TryServeLocked(key, version, out var cached))
            {
                return new ValueTask<AccessToken>(cached);
            }

            if (!_inFlight.TryGetValue(key, out acquisition) || acquisition.Version != version)
            {
                acquisition = new Acquisition(key.Detached(), version);
                _inFlight[key] = acquisition;
                started = true;
            }

            acquisition.Waiters++;
        }

        // Outside the gate: the inner credential runs up to its first await on this thread, as it does without the cache.
        if (started)
        {
            _ = RunAsync(acquisition, requestContext);
        }

        return new ValueTask<AccessToken>(WaitAsync(acquisition, started, cancellationToken));
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (Bypasses(requestContext))
        {
            return _inner.GetToken(requestContext, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = Key.Of(requestContext);
        int version;
        lock (_gate)
        {
            version = _invalidationVersion();
            if (TryServeLocked(key, version, out var cached))
            {
                return cached;
            }
        }

        var token = _inner.GetToken(requestContext, cancellationToken);
        lock (_gate)
        {
            if (version == _invalidationVersion() && IsFresh(token))
            {
                _entries[key.Detached()] = new Entry(token, version);
            }
        }

        return token;
    }

    private static bool Bypasses(TokenRequestContext context) =>
        !string.IsNullOrEmpty(context.Claims) || context.IsProofOfPossessionEnabled;

    private bool TryServeLocked(Key key, int version, out AccessToken token)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.Version == version && IsFresh(entry.Token))
            {
                token = entry.Token;
                return true;
            }

            // Stale, or acquired before an invalidation: it is never served again, so it does not stay in memory either.
            _entries.Remove(key);
        }

        token = default;
        return false;
    }

    private bool IsFresh(AccessToken token)
    {
        var now = _time.GetUtcNow();

        // Expiry is the ceiling: more than five minutes must remain, whatever the refresh hint says. A TimeSpan difference,
        // so a malformed expiry near DateTimeOffset.MinValue cannot underflow.
        if (token.ExpiresOn - now <= RefreshMargin)
        {
            return false;
        }

        return token.RefreshOn is not { } hint || now < hint;
    }

    private async Task RunAsync(Acquisition acquisition, TokenRequestContext context)
    {
        AccessToken token = default;
        Exception? failure = null;
        try
        {
            token = await _inner.GetTokenAsync(context, acquisition.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool abandoned;
        lock (_gate)
        {
            acquisition.Finished = true;
            abandoned = acquisition.Abandoned;
            if (_inFlight.TryGetValue(acquisition.Key, out var current) && ReferenceEquals(current, acquisition))
            {
                _inFlight.Remove(acquisition.Key);
            }

            // Stored only for a caller still waiting, under the version it was asked for, and only while it is fresh.
            if (failure is null && !abandoned && acquisition.Version == _invalidationVersion() && IsFresh(token))
            {
                _entries[acquisition.Key] = new Entry(token, acquisition.Version);
            }
        }

        if (abandoned)
        {
            acquisition.Result.TrySetCanceled();
        }
        else if (failure is not null)
        {
            acquisition.Result.TrySetException(failure);
        }
        else
        {
            acquisition.Result.TrySetResult(token);
        }

        acquisition.Cancellation.Dispose();
    }

    private async Task<AccessToken> WaitAsync(Acquisition acquisition, bool started, CancellationToken cancellationToken)
    {
        var waiting = System.Diagnostics.Stopwatch.GetTimestamp();
        var received = false;
        try
        {
            var token = await acquisition.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            received = true;
            return token;
        }
        finally
        {
            var leftUnfinished = Leave(acquisition);

            // A request that joined another's acquisition, or left its own before it ended, waited for a token that the
            // inner credential's numbers (recorded on the request that started it) do not give to this one.
            if (!started || leftUnfinished)
            {
                ReportSharedWait(System.Diagnostics.Stopwatch.GetElapsedTime(waiting), received);
            }
        }
    }

    private void ReportSharedWait(TimeSpan wait, bool received)
    {
        try
        {
            _sharedWait?.Invoke(wait, received);
        }
        catch (Exception)
        {
            // Diagnostics never change what a request gets.
        }
    }

    // The last waiter to leave an unfinished acquisition abandons it: out of the map, so no one joins it after, and
    // cancelled, so the inner credential stops as it did when its one caller gave up. Returns whether the acquisition was
    // still unfinished as this waiter left.
    private bool Leave(Acquisition acquisition)
    {
        var abandon = false;
        bool unfinished;
        lock (_gate)
        {
            unfinished = !acquisition.Finished;
            acquisition.Waiters--;
            if (acquisition.Waiters == 0 && !acquisition.Finished)
            {
                acquisition.Abandoned = true;
                abandon = true;
                if (_inFlight.TryGetValue(acquisition.Key, out var current) && ReferenceEquals(current, acquisition))
                {
                    _inFlight.Remove(acquisition.Key);
                }
            }
        }

        if (abandon)
        {
            try
            {
                acquisition.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The acquisition finished between the check and the cancel; there is nothing left to stop.
            }
        }

        return unfinished;
    }

    private readonly record struct Entry(AccessToken Token, int Version);

    // The scopes in order, the tenant and the CAE flag. A lookup holds the caller's scopes array, so a hit allocates
    // nothing; a key that is kept holds its own copy, so a caller that reuses or changes its array cannot change an entry.
    private readonly struct Key : IEquatable<Key>
    {
        private readonly string[] _scopes;
        private readonly string? _tenant;
        private readonly bool _cae;

        private Key(string[] scopes, string? tenant, bool cae)
        {
            _scopes = scopes;
            _tenant = tenant;
            _cae = cae;
        }

        public static Key Of(TokenRequestContext context) => new(context.Scopes ?? [], context.TenantId, context.IsCaeEnabled);

        public Key Detached() => new((string[])_scopes.Clone(), _tenant, _cae);

        public bool Equals(Key other)
        {
            if (_cae != other._cae
                || !string.Equals(_tenant, other._tenant, StringComparison.Ordinal)
                || _scopes.Length != other._scopes.Length)
            {
                return false;
            }

            for (var i = 0; i < _scopes.Length; i++)
            {
                if (!string.Equals(_scopes[i], other._scopes[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var scope in _scopes)
            {
                hash.Add(scope, StringComparer.Ordinal);
            }

            hash.Add(_tenant, StringComparer.Ordinal);
            hash.Add(_cae);
            return hash.ToHashCode();
        }
    }

    private sealed class Acquisition
    {
        public Acquisition(Key key, int version)
        {
            Key = key;
            Version = version;

            // A failure that reaches no waiter (the last one was cancelled as it came) must not surface as an unobserved
            // task exception, which the app logs as an error.
            _ = Result.Task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Key Key { get; }

        public int Version { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<AccessToken> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The three below are guarded by the credential's gate.
        public int Waiters { get; set; }

        public bool Finished { get; set; }

        public bool Abandoned { get; set; }
    }
}