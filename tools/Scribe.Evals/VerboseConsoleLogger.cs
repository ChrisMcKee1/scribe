using Microsoft.Extensions.Logging;

namespace Scribe.Evals;

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that writes to stderr, enabled by <c>--verbose</c>. It surfaces the
/// init/cleanup diagnostics <see cref="Scribe.Core.Cleanup.TextCleanupService"/> logs — which are
/// otherwise swallowed by <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> — so a
/// failure such as a Foundry Local startup exception is visible (with its stack trace) when triaging an
/// eval run instead of collapsing to a bare "Unavailable" status.
/// </summary>
internal sealed class VerboseConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = logLevel >= LogLevel.Error ? ConsoleColor.Red
            : logLevel >= LogLevel.Warning ? ConsoleColor.Yellow
            : ConsoleColor.DarkGray;
        Console.Error.WriteLine($"   [log:{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.Error.WriteLine(exception);
        }
        Console.ForegroundColor = previous;
    }
}
