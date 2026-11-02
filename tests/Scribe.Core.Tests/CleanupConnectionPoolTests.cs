using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests;

/// <summary>
/// PerfFlags.KeepIdleCleanupConnections: every cleanup client's shared transport keeps an idle connection for minutes
/// instead of .NET's one, so a dictation after a pause skips a new connection and TLS handshake. Off must be exactly the
/// handler every release before used; on, only the two pool times change, the hand-off stays just in front of the
/// network, no redirect is followed and nothing is sent that would not have been.
/// </summary>
public sealed class CleanupConnectionPoolTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void Off_the_shared_transport_sends_through_the_handler_every_release_before_used()
    {
        var network = Assert.IsType<HttpClientHandler>(VocabularyHandOffHandler.SharedNetworkFor(keepIdleConnections: false));

        Assert.False(network.AllowAutoRedirect);
        Assert.Same(network, VocabularyHandOffHandler.SharedNetworkFor(keepIdleConnections: false));
        Assert.Same(
            VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: false),
            VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: false));
    }

    [Fact]
    public void On_the_shared_transport_keeps_idle_connections_longer_and_bounds_their_lifetime()
    {
        var network = Assert.IsType<SocketsHttpHandler>(VocabularyHandOffHandler.SharedNetworkFor(keepIdleConnections: true));
        using var dotnetDefault = new SocketsHttpHandler();

        Assert.False(network.AllowAutoRedirect);
        Assert.Equal(VocabularyHandOffHandler.KeptIdleTimeout, network.PooledConnectionIdleTimeout);
        Assert.True(network.PooledConnectionIdleTimeout > dotnetDefault.PooledConnectionIdleTimeout);
        Assert.Equal(VocabularyHandOffHandler.KeptConnectionLifetime, network.PooledConnectionLifetime);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, network.PooledConnectionLifetime);
        Assert.True(network.PooledConnectionLifetime >= network.PooledConnectionIdleTimeout);

        // One of each for the process, and the two never share a pool.
        Assert.Same(network, VocabularyHandOffHandler.SharedNetworkFor(keepIdleConnections: true));
        Assert.Same(
            VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: true),
            VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: true));
        Assert.NotSame(
            VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: true),
            VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: false));
    }

    [Fact]
    public void On_every_other_setting_is_what_the_old_handler_runs_with()
    {
        // HttpClientHandler runs on a SocketsHttpHandler it configures from its own properties; the flag's handler must
        // reach the service the same way (same proxy, cookies, credentials, decompression, certificate checks, limits).
        using var keeping = Assert.IsType<SocketsHttpHandler>(VocabularyHandOffHandler.CreateNetworkHandler(keepIdleConnections: true));
        using var old = Assert.IsType<HttpClientHandler>(VocabularyHandOffHandler.CreateNetworkHandler(keepIdleConnections: false));

        Assert.Equal(old.AllowAutoRedirect, keeping.AllowAutoRedirect);
        Assert.Equal(old.UseProxy, keeping.UseProxy);
        Assert.Equal(old.Proxy, keeping.Proxy);
        Assert.Equal(old.DefaultProxyCredentials, keeping.DefaultProxyCredentials);
        Assert.Equal(old.UseCookies, keeping.UseCookies);
        Assert.Equal(old.Credentials, keeping.Credentials);
        Assert.Equal(old.PreAuthenticate, keeping.PreAuthenticate);
        Assert.Equal(old.AutomaticDecompression, keeping.AutomaticDecompression);
        Assert.Equal(old.MaxConnectionsPerServer, keeping.MaxConnectionsPerServer);
        Assert.Equal(old.MaxResponseHeadersLength, keeping.MaxResponseHeadersLength);
        Assert.Equal(old.MaxAutomaticRedirections, keeping.MaxAutomaticRedirections);
        Assert.False(old.CheckCertificateRevocationList);
        Assert.Equal(X509RevocationMode.NoCheck, keeping.SslOptions.CertificateRevocationCheckMode);
        Assert.Null(keeping.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(keeping.ConnectCallback);
        Assert.Null(keeping.PlaintextStreamFilter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_service_chooses_the_shared_transport_its_flag_names(bool on)
    {
        var flags = on ? PerfFlags.Parse(PerfFlags.KeepIdleCleanupConnections) : PerfFlags.None;
        await using var service = new TextCleanupService(NullLogger<TextCleanupService>.Instance, perfFlags: flags);

        Assert.Same(VocabularyHandOffHandler.SharedTransportFor(keepIdleConnections: on), service.HandOffTransport());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Either_way_the_hand_off_stays_in_front_of_the_network_no_redirect_is_followed_and_a_connection_is_reused(bool keepIdle)
    {
        await using var server = new LoopbackServer();
        using var network = VocabularyHandOffHandler.CreateNetworkHandler(keepIdle);
        using var transport = VocabularyHandOffHandler.CreateTransport(network);

        // No admission: refused before anything opens a connection.
        var refused = await Record.ExceptionAsync(() => SendAsync(transport, server.Url("/refused")));
        Assert.False(Assert.IsType<VocabularyHandOffRefusedException>(VocabularyHandOffRefusedException.Find(refused)).Admitted);
        Assert.Equal(0, server.Connections);

        using (new CleanupAdmission(CleanupRequestKind.Probe, AiVocabularyScope.None, null).Enter())
        {
            // The answer is a redirect, which comes back as the answer: following it would carry the key elsewhere.
            Assert.Equal(302, await SendAsync(transport, server.Url("/first")).WaitAsync(Bound));
            Assert.Equal(302, await SendAsync(transport, server.Url("/second")).WaitAsync(Bound));
        }

        Assert.Equal(["/first", "/second"], server.Paths);
        Assert.Equal(1, server.Connections);
    }

    private static async Task<int> SendAsync(System.ClientModel.Primitives.HttpClientPipelineTransport transport, Uri url)
    {
        using var message = transport.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = url;
        await transport.ProcessAsync(message);
        return message.Response!.Status;
    }

    // A loopback HTTP/1.1 server that keeps every connection open and answers every request with a redirect to a path it
    // never asked for, so a client that followed it would show up in Paths.
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly ConcurrentBag<Task> _serving = [];
        private readonly Task _accepting;
        private int _connections;

        internal LoopbackServer()
        {
            _listener.Start();
            _accepting = AcceptAsync();
        }

        internal int Connections => Volatile.Read(ref _connections);

        internal string[] Paths => [.. _paths];

        internal Uri Url(string path) => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    _serving.Add(ServeAsync(client));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (await reader.ReadLineAsync(_stop.Token) is { } requestLine)
                    {
                        while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 })
                        {
                        }

                        _paths.Enqueue(requestLine.Split(' ')[1]);
                        var answer = $"HTTP/1.1 302 Found\r\nLocation: {Url("/elsewhere")}\r\nContent-Length: 0\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                    }
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                    // The client closed the connection.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_serving);
            _stop.Dispose();
        }
    }
}
