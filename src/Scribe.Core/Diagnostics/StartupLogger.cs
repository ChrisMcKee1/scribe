using System.Collections;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// A logger for a service made before the host exists (DATA-O-05b's early database): it writes straight to the file log's
/// provider until <see cref="Attach"/> hands it the host's logger factory, and from then on through the host's pipeline,
/// as every other <see cref="ILogger{TCategoryName}"/> does. The category is the one the host gives the same type.
/// </summary>
/// <remarks>
/// A failure its caller logs is handed on as its shape (<see cref="FailureShape.DescribeWithStack"/>: the exception types,
/// the codes .NET, Windows and SQLite attach, and the stack frames) on the lines after the message, where the exception
/// used to be, and never as the exception object, whose message is not safe to log (LogPrivacyGuardTests,
/// DATA-IMPL-A-05). A structured provider also finds the shape among the entry's values, as <see cref="FailureKey"/>.
/// </remarks>
public sealed class StartupLogger<T> : ILogger<T>
{
    /// <summary>The name the failure's shape has among an entry's values.</summary>
    public const string FailureKey = "Failure";

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

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var current = _current;
        if (exception is null)
        {
            current.Log(logLevel, eventId, state, null, formatter);
            return;
        }

        var shaped = new ShapedFailure<TState>(state, formatter, FailureShape.DescribeWithStack(exception));
        current.Log(logLevel, eventId, shaped, null, ShapedFailure<TState>.Format);
    }

    // The caller's entry with its failure's shape in place of the exception: the caller's message, then the shape on the
    // following lines, as the file log renders an exception; the caller's values, then the shape as one more.
    private sealed class ShapedFailure<TState> : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly TState _state;
        private readonly Func<TState, Exception?, string> _formatter;
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _values;
        private readonly string _shape;

        public ShapedFailure(TState state, Func<TState, Exception?, string> formatter, string shape)
        {
            _state = state;
            _formatter = formatter;
            _shape = shape;
            _values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        }

        public int Count => _values.Count + 1;

        public KeyValuePair<string, object?> this[int index] =>
            index == _values.Count ? new KeyValuePair<string, object?>(FailureKey, _shape) : _values[index];

        // The caller's formatter is not handed the exception either, so one that would render it has nothing to render.
        public static string Format(ShapedFailure<TState> entry, Exception? _) =>
            entry._formatter(entry._state, null) + Environment.NewLine + entry._shape;

        public override string ToString() => Format(this, null);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
