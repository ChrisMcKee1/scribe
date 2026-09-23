using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// The cleanup tests' own log capture, in a namespace of its own: the lifecycle tests keep a different
// CapturingLogger and CapturedLogEntry in Scribe.Core.Tests.Concurrency, and a same-named type in the root test
// namespace would silently take the place of theirs.
namespace Scribe.Core.Tests.CleanupLogging;

/// <summary>
/// One captured log call: the rendered message, the exception as the file sink would write it
/// (<c>ToString()</c>), and every structured state value, because a structured sink keeps arguments
/// that a message template alone would hide.
/// </summary>
internal sealed record CapturedLogEntry(LogLevel Level, string Message, string? Exception, IReadOnlyList<string> State)
{
    public string AllText => string.Join('\n', new[] { Message, Exception ?? string.Empty }.Concat(State));
}

/// <summary>Records every log call a service makes, thread-safely, so a test can search all of it.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public string AllText => string.Join("\n----\n", Entries.Select(entry => entry.AllText));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = new List<string>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                values.Add($"{pair.Key}={pair.Value}");
            }
        }

        _entries.Enqueue(new CapturedLogEntry(logLevel, formatter(state, exception), exception?.ToString(), values));
    }
}
