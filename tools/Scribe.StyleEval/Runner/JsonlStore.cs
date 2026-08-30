using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Append-only JSONL file with a resume index, one JSON object per line.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of the suite are long, paid, interruptible runs that write one row per completed
/// unit of work, so both need the same three properties: a row hits the disk the moment it is
/// produced, a truncated final line left by a crash is skipped rather than fatal, and restarting
/// reads the file back to skip what is already there. Generation results and judge verdicts differ
/// only in the row type, which is why that type is the parameter.
/// </para>
/// <para>
/// One writer task owns the file handle. Locking a shared stream from every worker instead would
/// serialise the workers on disk I/O for no benefit.
/// </para>
/// </remarks>
/// <typeparam name="T">The row type. One line of the file is one serialized <typeparamref name="T"/>.</typeparam>
internal class JsonlStore<T> : IAsyncDisposable
{
    /// <summary>Shared serializer settings, so a file written here is readable by the same reader.</summary>
    protected static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Task _writer;
    private readonly string _path;

    public JsonlStore(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queues a completed row. Returns as soon as it is queued, never blocks on the disk.</summary>
    public void Append(T row) => _channel.Writer.TryWrite(row);

    private async Task WriteLoopAsync()
    {
        await using var stream = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await foreach (var row in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, Json)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads every well-formed row back.</summary>
    public static IEnumerable<T> ReadRows(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in ReadLinesShared(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            T? row = default;
            try
            {
                row = JsonSerializer.Deserialize<T>(line, Json);
            }
            catch (JsonException)
            {
                // A crash mid-write leaves one truncated line. Skip it, do not stop the read.
            }

            if (row is not null)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// The keys already present in <paramref name="path"/>, as named by <paramref name="key"/>.
    /// </summary>
    public static IReadOnlySet<string> LoadKeys(string path, Func<T, string?> key, out int malformedLines)
    {
        ArgumentNullException.ThrowIfNull(key);

        malformedLines = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            return keys;
        }

        foreach (var line in ReadLinesShared(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var row = JsonSerializer.Deserialize<T>(line, Json);
                if (row is not null && key(row) is { Length: > 0 } value)
                {
                    keys.Add(value);
                }
            }
            catch (JsonException)
            {
                malformedLines++;
            }
        }

        return keys;
    }

    /// <summary>
    /// Reads the file while another process, or this one, still holds it open for writing.
    /// <c>File.ReadLines</c> opens with <c>FileShare.Read</c>, which loses to the writer's handle.
    /// </summary>
    protected static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
