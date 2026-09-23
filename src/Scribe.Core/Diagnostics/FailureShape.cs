using System.Text;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// What the shared log records about a failure anywhere outside Cleanup, the app shell included: its shape, never
/// its text. <see cref="CleanupFailureShape"/> does the work, so a Settings failure and a cleanup failure read alike.
/// </summary>
/// <remarks>
/// An exception's message is not safe to log from anywhere near a provider or the user's words. .NET 10 appends
/// the endpoint to a connection failure as "(host:port)", Azure and Entra errors quote the account, the tenant and
/// resource names, a failed shell launch quotes the whole command it was given (for the AI report's mailto:, the
/// report itself), and a duplicate-key failure quotes the key. So the log keeps the exception types, the codes
/// .NET, Windows and SQLite attach, and, where a failure points at a defect, its stack frames, which name code
/// and nothing else; every message is left out. This is the live form of what <see cref="HistoricalLogRedaction"/>
/// does to entries older builds wrote.
/// </remarks>
public static class FailureShape
{
    private const string InnerSeparator = "   --- End of inner exception stack trace ---";
    private const string AsyncSeparator = "--- End of stack trace from previous location";

    // Enough frames for any real trace, bounded because this runs while a failure is being reported.
    private const int MaxFrames = 96;
    private const int MaxChain = 8;

    /// <summary>
    /// One line, such as <c>SqliteException(5/5)</c> or
    /// <c>HttpRequestException(NameResolutionError) inner=SocketException(HostNotFound)</c>. Never throws.
    /// </summary>
    public static string Describe(Exception? exception) => CleanupFailureShape.Describe(exception);

    /// <summary>
    /// <see cref="Describe"/>, then on the following lines the stack frames of the failure and of the exceptions
    /// inside it, innermost first as <see cref="Exception.ToString"/> orders them. Only frame lines and the
    /// separators between them are kept, so nothing a trace could otherwise carry gets through. For failures that
    /// point at a defect, where the frames are the diagnosis. Never throws.
    /// </summary>
    public static string DescribeWithStack(Exception? exception)
    {
        var shape = Describe(exception);
        if (exception is null)
        {
            return shape;
        }

        try
        {
            var chain = new List<Exception>(MaxChain);
            for (var current = exception; current is not null && chain.Count < MaxChain; current = current.InnerException)
            {
                chain.Add(current);
            }

            var builder = new StringBuilder(shape);
            var frames = 0;
            for (var i = chain.Count - 1; i >= 0 && frames < MaxFrames; i--)
            {
                var wrote = false;
                foreach (var line in (chain[i].StackTrace ?? string.Empty).Split('\n'))
                {
                    if (frames == MaxFrames)
                    {
                        break;
                    }

                    var trimmed = line.Trim();
                    if (IsFrame(trimmed) || IsAsyncSeparator(trimmed))
                    {
                        builder.Append(Environment.NewLine).Append("   ").Append(trimmed);
                        frames++;
                        wrote = true;
                    }
                }

                if (wrote && i > 0)
                {
                    builder.Append(Environment.NewLine).Append(InnerSeparator);
                }
            }

            return builder.ToString();
        }
        catch (Exception)
        {
            // Reporting a failure must never become one; the shape alone is still the useful part.
            return shape;
        }
    }

    // "at Namespace.Type.Method(ParameterType name) in path:line 12": a method, its parameter types and a
    // source position, never a value.
    private static bool IsFrame(string trimmed) =>
        trimmed.Length > 3 && trimmed.StartsWith("at ", StringComparison.Ordinal) && !char.IsWhiteSpace(trimmed[3]);

    // Where an async method resumed; older runtimes added "where exception was thrown".
    private static bool IsAsyncSeparator(string trimmed) =>
        trimmed.StartsWith(AsyncSeparator, StringComparison.Ordinal) && trimmed.EndsWith("---", StringComparison.Ordinal);
}
