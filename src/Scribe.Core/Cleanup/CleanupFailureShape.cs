using System.ClientModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Describes a provider failure by its shape: exception types, HTTP status, a service error code and
/// the enum-valued reasons .NET attaches to network failures.
/// </summary>
/// <remarks>
/// The file log writes an exception's <c>ToString()</c>, and the exceptions an HTTP or SDK client
/// throws routinely embed the endpoint: <c>HttpRequestException</c> appends <c>(host:port)</c>, a
/// <c>ClientResultException</c> carries whatever the server put in its error body, and an ARM
/// <c>RequestFailedException</c> quotes the full resource id. None of that may reach the shared log
/// (see the privacy contract in PRIVACY.md), so the log records this shape instead. It keeps the part
/// that diagnoses a failure (a 403 against a 404, a DNS failure against a refused connection) and
/// drops the part that identifies the user's configuration.
/// </remarks>
internal static partial class CleanupFailureShape
{
    // Real client exception chains are a handful deep. The bound matters because this runs while a
    // failure is already being reported, where a pathological chain must cost a fixed amount of work.
    private const int MaxNodes = 32;
    private const int MaxChainEntries = 8;

    // A service error code is an identifier such as "DeploymentNotFound" or "invalid_api_key".
    // Rejecting dots, colons, slashes and spaces is what keeps a URL, a dotted host name or a
    // sentence from being passed off as a code by an endpoint Scribe does not control. Generated at
    // build time, like the Entra pattern below: a runtime-compiled pattern paid its compile and first
    // match on the first failure a dictation reported.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex SafeCode();

    // Entra error codes are fixed identifiers, and they are the difference between "wrong secret"
    // and "secret not active yet". The rest of an Entra message names the tenant and the client.
    [GeneratedRegex(@"\bAADSTS\d{4,9}\b")]
    private static partial Regex AadstsCode();

    /// <summary>
    /// A one-line, diagnostics-safe description such as
    /// <c>ClientResultException status=400 code=invalid_request_error inner=HttpRequestException</c>.
    /// </summary>
    /// <param name="exception">The failure to describe.</param>
    /// <param name="kind">An optional classification the caller already made, e.g. <c>model-not-loaded</c>.</param>
    public static string Describe(Exception? exception, string? kind = null)
    {
        if (exception is null)
        {
            return "none";
        }

        try
        {
            var parts = new List<string>(6) { Label(exception) };

            var status = ExtractHttpStatus(exception);
            if (status > 0)
            {
                parts.Add("status=" + status);
            }

            if (ExtractErrorCode(exception) is { } code)
            {
                parts.Add("code=" + code);
            }

            var aadsts = ExtractAadstsCodes(exception);
            if (aadsts.Count > 0)
            {
                parts.Add("aadsts=" + string.Join(",", aadsts));
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                parts.Add("kind=" + kind);
            }

            var chain = InnerLabels(exception);
            if (chain.Count > 0)
            {
                parts.Add("inner=" + string.Join(">", chain));
            }

            return string.Join(' ', parts);
        }
        catch (Exception)
        {
            // A diagnostics helper must never be the thing that fails. The type name alone is still
            // safe and still better than nothing.
            return exception.GetType().Name;
        }
    }

    /// <summary>
    /// The HTTP status from the two exception shapes the Azure and OpenAI clients throw, including
    /// when the Agent Framework wraps them. Zero when the failure was not an HTTP response.
    /// </summary>
    internal static int ExtractHttpStatus(Exception? exception)
    {
        foreach (var current in Walk(exception))
        {
            switch (current)
            {
                case ClientResultException client:
                    return client.Status;
                case Azure.RequestFailedException request:
                    return request.Status;
            }
        }

        return 0;
    }

    /// <summary>
    /// The service's own error code (<c>RequestFailedException.ErrorCode</c>, or <c>error.code</c> /
    /// <c>error.type</c> from an OpenAI-style error body), or null when there is none that passes
    /// <see cref="SanitizeCode"/>.
    /// </summary>
    internal static string? ExtractErrorCode(Exception? exception)
    {
        foreach (var current in Walk(exception))
        {
            switch (current)
            {
                case Azure.RequestFailedException request when SanitizeCode(request.ErrorCode) is { } azureCode:
                    return azureCode;
                case ClientResultException client when SanitizeCode(ReadEnvelopeCode(client)) is { } clientCode:
                    return clientCode;
            }
        }

        return null;
    }

    /// <summary>Returns <paramref name="value"/> when it looks like an identifier, otherwise null.</summary>
    internal static string? SanitizeCode(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && SafeCode().IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// An exception's own message, or null for an aggregate. <c>AggregateException.Message</c>
    /// concatenates every inner message recursively, which is unbounded work (and a stack overflow)
    /// on a deeply nested failure, and <see cref="Walk"/> visits those inner exceptions itself.
    /// </summary>
    internal static string? MessageOf(Exception exception) =>
        exception is AggregateException ? null : exception.Message;

    /// <summary>
    /// Every exception reachable from <paramref name="root"/> through inner exceptions and aggregate
    /// inner lists, outermost first, depth first, visiting at most <see cref="MaxNodes"/> in total.
    /// </summary>
    /// <remarks>
    /// One walk with one total budget. A search that recursed into each aggregate's list and then
    /// also carried on down the same chain did work exponential in its depth bound on a deeply
    /// nested failure, and this only ever runs while a failure is already being reported.
    /// </remarks>
    internal static IEnumerable<Exception> Walk(Exception? root)
    {
        if (root is null)
        {
            yield break;
        }

        var pending = new Stack<Exception>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited < MaxNodes)
        {
            var current = pending.Pop();
            visited++;
            yield return current;

            if (current is AggregateException aggregate)
            {
                // Its InnerException is the first entry of this list, so the list covers it.
                var inner = aggregate.InnerExceptions;
                for (var i = Math.Min(inner.Count, MaxNodes) - 1; i >= 0; i--)
                {
                    pending.Push(inner[i]);
                }
            }
            else if (current.InnerException is { } next)
            {
                pending.Push(next);
            }
        }
    }

    private static IReadOnlyList<string> ExtractAadstsCodes(Exception exception)
    {
        var codes = new List<string>(2);
        foreach (var current in Walk(exception))
        {
            foreach (Match match in AadstsCode().Matches(MessageOf(current) ?? string.Empty))
            {
                if (!codes.Contains(match.Value, StringComparer.Ordinal))
                {
                    codes.Add(match.Value);
                }

                if (codes.Count == 3)
                {
                    return codes;
                }
            }
        }

        return codes;
    }

    private static List<string> InnerLabels(Exception exception) =>
        Walk(exception).Skip(1).Take(MaxChainEntries).Select(Label).ToList();

    // The enum-valued reasons are safe by construction: they are .NET's own vocabulary, not text
    // from the endpoint, and "NameResolutionError" against "ConnectionRefused" is most of a diagnosis.
    // A plain IOException's HResult plays the same part for local files: a sharing violation against
    // a full disk, without the path (and so the user name) its message carries. The Windows error of a
    // Win32Exception and the HRESULT of a COM or other external failure do the same for a clipboard
    // held open, a missing shell handler or a lost audio endpoint, and the SQLite result codes for the
    // database: once the message is gone, the code is the diagnosis.
    private static string Label(Exception exception) => exception switch
    {
        SocketException socket => $"SocketException({socket.SocketErrorCode})",
        HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http =>
            $"HttpRequestException({http.HttpRequestError})",
        IOException io when io.GetType() == typeof(IOException) => $"IOException(0x{io.HResult:X8})",
        SqliteException sqlite => $"SqliteException({sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode})",
        Win32Exception win32 => $"{win32.GetType().Name}({win32.NativeErrorCode})",
        ExternalException external => $"{external.GetType().Name}(0x{external.HResult:X8})",
        _ => exception.GetType().Name,
    };

    // Non-throwing: reading a raw response can fail on a non-buffered or disposed response, and this
    // only runs while a failure is already being reported.
    private static string? ReadEnvelopeCode(ClientResultException client)
    {
        try
        {
            var body = client.GetRawResponse()?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "code", "type" })
            {
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                    SanitizeCode(value.GetString()) is { } code)
                {
                    return code;
                }
            }
        }
        catch (Exception)
        {
            // Not JSON, or not the envelope we know. No code is a complete answer.
        }

        return null;
    }
}
