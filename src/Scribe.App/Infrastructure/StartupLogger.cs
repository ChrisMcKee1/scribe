using Microsoft.Extensions.Logging;

namespace Scribe.App.Infrastructure;

/// <summary>
/// A logger for a service made before the host exists (DATA-O-05b's early database): it writes straight to the file log's
/// provider until <see cref="Attach"/> hands it the host's logger factory, and from then on through the host's pipeline,
/// as every other <see cref="ILogger{TCategoryName}"/> does. The category is the one the host gives the same type.
/// </summary>
internal sealed class StartupLogger<T> : ILogger<T>
{
    private volatile ILogger _current;

    public StartupLogger(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _current = provider.CreateLogger(typeof(T).FullName!);
    }

    /// <summary>From now on, logs through <paramref name="factory"/>, the host's.</summary>
    public void Attach(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _current = factory.CreateLogger<T>();
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _current.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _current.IsEnabled(logLevel);

    // Scribe's own log calls hand a logger no exception object (LogPrivacyGuardTests), so none is forwarded either.
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _current.Log(logLevel, eventId, state, null, formatter);
}
