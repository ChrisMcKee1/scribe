using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-IMPL-A-05: the early database's logger hands a failure on as its shape, before the host exists and after
/// <see cref="StartupLogger{T}.Attach"/> alike: the type, the SQLite codes and the frames, never the message, and never
/// the exception object.
/// </summary>
public sealed class StartupLoggerTests
{
    private const string SyntheticMessage = "synthetic diagnostic failure";
    private const string Template = "Database probe hit a transient error (attempt {Attempt}/{Max}); retrying.";
    private const string Rendered = "Database probe hit a transient error (attempt 1/3); retrying.";

    [Fact]
    public void A_failure_logged_before_the_host_exists_reaches_the_file_log_as_its_shape()
    {
        var early = new RenderingProvider();
        var logger = new StartupLogger<ScribeDatabase>(early);

        logger.LogWarning(ThrownFailure(), Template, 1, 3);

        AssertShapedOnly(Assert.Single(early.Entries));
    }

    [Fact]
    public void A_failure_logged_after_attach_reaches_the_host_s_pipeline_as_its_shape()
    {
        var early = new RenderingProvider();
        var host = new RenderingProvider();
        var logger = new StartupLogger<ScribeDatabase>(early);
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(host));
        logger.Attach(factory);

        logger.LogWarning(ThrownFailure(), Template, 1, 3);

        Assert.Empty(early.Entries);
        var entry = Assert.Single(host.Entries);
        Assert.Equal(typeof(ScribeDatabase).FullName, entry.Category);
        AssertShapedOnly(entry);
    }

    [Fact]
    public void A_failure_inside_another_keeps_both_shapes_and_neither_message()
    {
        var early = new RenderingProvider();
        var logger = new StartupLogger<ScribeDatabase>(early);
        var failure = new InvalidOperationException("outer " + SyntheticMessage, new SqliteException(SyntheticMessage, 11));

        logger.LogError(failure, "Database salvage failed; starting fresh. The damaged file is kept alongside.");

        var entry = Assert.Single(early.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains("InvalidOperationException", entry.Line, StringComparison.Ordinal);
        Assert.Contains("SqliteException(11/11)", entry.Line, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticMessage, entry.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_without_a_failure_is_handed_on_as_it_was()
    {
        var early = new RenderingProvider();
        var logger = new StartupLogger<ScribeDatabase>(early);

        logger.LogInformation(Template, 1, 3);

        var entry = Assert.Single(early.Entries);
        Assert.Null(entry.Exception);
        Assert.EndsWith("ScribeDatabase: " + Rendered, entry.Line, StringComparison.Ordinal);
        Assert.DoesNotContain(StartupLogger<ScribeDatabase>.FailureKey, entry.Values.Select(value => value.Key));
    }

    [Fact]
    public void A_structured_provider_finds_the_caller_s_values_and_the_shape()
    {
        var early = new RenderingProvider();
        var logger = new StartupLogger<ScribeDatabase>(early);

        logger.LogWarning(ThrownFailure(), Template, 1, 3);

        var values = Assert.Single(early.Entries).Values.ToDictionary(value => value.Key, value => value.Value);
        Assert.Equal(1, values["Attempt"]);
        Assert.Equal(3, values["Max"]);
        Assert.Equal(Template, values["{OriginalFormat}"]);
        var shape = Assert.IsType<string>(values[StartupLogger<ScribeDatabase>.FailureKey]);
        Assert.StartsWith("SqliteException(5/5)", shape, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticMessage, shape, StringComparison.Ordinal);
    }

    // The entry as the file log writes it: its message, and the shape on the lines after it, where the exception was.
    private static void AssertShapedOnly(Entry entry)
    {
        Assert.Null(entry.Exception);
        var lines = entry.Line.Split(Environment.NewLine);
        Assert.EndsWith("ScribeDatabase: " + Rendered, lines[0], StringComparison.Ordinal);
        Assert.Equal("SqliteException(5/5)", lines[1]);
        Assert.Contains(lines.Skip(2), line => line.StartsWith("   at ", StringComparison.Ordinal)
            && line.Contains(nameof(ThrownFailure), StringComparison.Ordinal));
        Assert.DoesNotContain(SyntheticMessage, entry.Line, StringComparison.Ordinal);
    }

    // SQLITE_BUSY, thrown so that it has frames to keep.
    private static SqliteException ThrownFailure()
    {
        try
        {
            throw new SqliteException(SyntheticMessage, 5);
        }
        catch (SqliteException caught)
        {
            return caught;
        }
    }

    private sealed record Entry(string Category, string Line, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> Values);

    // Renders each entry as FileLoggerProvider does (LogLineFormat, the formatter handed the exception it was given), and
    // keeps what a provider is handed.
    private sealed class RenderingProvider : ILoggerProvider
    {
        public List<Entry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Renderer(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Renderer(RenderingProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = LogLineFormat.Format(
                    DateTime.Now, logLevel, LogLineFormat.ShortCategory(category), formatter(state, exception), exception);
                var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                lock (owner.Entries)
                {
                    owner.Entries.Add(new Entry(category, line, exception, [.. values]));
                }
            }
        }
    }
}
