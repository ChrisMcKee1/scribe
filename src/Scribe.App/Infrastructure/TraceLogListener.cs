using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Bridges Scribe's finished spans to the file log without the OpenTelemetry SDK, when
/// <see cref="PerfFlags.LightTraceBridge"/> is on and nothing configures OpenTelemetry (DATA-O-03). Starts listening when the
/// host starts, as the SDK's TracerProvider does, and stops when the host's services are disposed, as it does; the lines
/// are <see cref="TraceLogBridge.Write"/>'s, the same the SDK path writes. References no OpenTelemetry type, so none of its
/// assemblies loads on this path.
/// </summary>
internal sealed class TraceLogListener : IHostedService, IDisposable
{
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private IDisposable? _listener;
    private bool _disposed;

    public TraceLogListener(ILoggerFactory loggerFactory) =>
        _log = loggerFactory.CreateLogger(TraceLogBridge.Category);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _listener ??= TraceLogBridge.Listen(_log);
            }
        }

        return Task.CompletedTask;
    }

    // The SDK's provider keeps listening through host stop and ends with the service provider; so does this.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        IDisposable? listener;
        lock (_gate)
        {
            _disposed = true;
            listener = _listener;
            _listener = null;
        }

        listener?.Dispose();
    }
}
