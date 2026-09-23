using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// What the app shell's log lines say about a failure: types, codes and frames, never the text an exception
/// carried from an endpoint, Azure, a shell launch or the user's own content.
/// </summary>
public sealed class FailureShapeTests
{
    private const string Host = "contoso-ai.openai.azure.com";
    private const string Canary = "canary-7f3a quarterly numbers for Dana";

    [Fact]
    public void A_connection_failure_keeps_its_diagnosis_and_loses_the_host()
    {
        var failure = new HttpRequestException(
            HttpRequestError.NameResolutionError, $"No such host is known. ({Host}:443)", new SocketException(11001));

        var shape = FailureShape.Describe(failure);

        Assert.Equal("HttpRequestException(NameResolutionError) inner=SocketException(HostNotFound)", shape);
    }

    [Fact]
    public void Local_failures_keep_the_code_that_is_their_diagnosis()
    {
        // A clipboard another process holds open, a shell with no handler for a mailto:, a locked database.
        var clipboard = new COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
        var launch = new Win32Exception(1155, $"An error occurred trying to start process 'mailto:x?body={Canary}'.");
        var database = new SqliteException($"SQLite Error 5: '{Canary}'.", 5, 5);

        Assert.Equal("COMException(0x800401D0)", FailureShape.Describe(clipboard));
        Assert.Equal("Win32Exception(1155)", FailureShape.Describe(launch));
        Assert.Equal("SqliteException(5/5)", FailureShape.Describe(database));
    }

    [Fact]
    public void The_stack_form_keeps_every_frame_innermost_first_and_no_message()
    {
        var failure = Catch(() => Outer());

        var text = FailureShape.DescribeWithStack(failure);

        var lines = text.Split(Environment.NewLine);
        Assert.Equal(FailureShape.Describe(failure), lines[0]);
        Assert.Equal("InvalidOperationException inner=ArgumentException", lines[0]);
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", text, StringComparison.OrdinalIgnoreCase);

        var inner = Array.FindIndex(lines, l => l.Contains(nameof(Inner), StringComparison.Ordinal));
        var separator = Array.IndexOf(lines, "   --- End of inner exception stack trace ---");
        var outer = Array.FindLastIndex(lines, l => l.Contains(nameof(Outer), StringComparison.Ordinal));
        Assert.True(inner > 0 && inner < separator && separator < outer, text);
        Assert.All(lines.Skip(1), l => Assert.Matches(@"^   (at \S|--- End of )", l));
    }

    [Fact]
    public void An_exception_that_was_never_thrown_is_described_by_its_shape_alone()
    {
        var failure = new InvalidOperationException(Canary);

        Assert.Equal("InvalidOperationException", FailureShape.DescribeWithStack(failure));
        Assert.Equal("none", FailureShape.DescribeWithStack(null));
    }

    [Fact]
    public void Only_frame_lines_of_a_trace_are_kept()
    {
        var failure = new ScriptedTraceException(
            "   at Scribe.App.Settings.SettingsWindow.VerifyAzureApiKeyAsync()" + Environment.NewLine +
            Canary + Environment.NewLine +
            "--- End of stack trace from previous location ---" + Environment.NewLine +
            "   at  " + Environment.NewLine +
            "   at System.Net.Http.HttpClient.SendAsync()");

        var lines = FailureShape.DescribeWithStack(failure).Split(Environment.NewLine);

        Assert.Equal(
            [
                nameof(ScriptedTraceException),
                "   at Scribe.App.Settings.SettingsWindow.VerifyAzureApiKeyAsync()",
                "   --- End of stack trace from previous location ---",
                "   at System.Net.Http.HttpClient.SendAsync()",
            ],
            lines);
    }

    [Fact]
    public void A_deep_trace_is_bounded()
    {
        var failure = Catch(() => Recurse(400));

        var frames = FailureShape.DescribeWithStack(failure).Split(Environment.NewLine).Length - 1;

        Assert.InRange(frames, 50, 96);
    }

    [Fact]
    public void A_trace_that_cannot_be_read_still_yields_the_shape()
    {
        Assert.Equal(nameof(ThrowingTraceException), FailureShape.DescribeWithStack(new ThrowingTraceException()));
    }

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected a failure.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Outer()
    {
        try
        {
            Inner();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Wrapped: {Canary}", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Inner() => throw new ArgumentException(Canary);

    // The addition after the call keeps it out of tail position, so every level stays on the stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int depth) =>
        depth == 0 ? throw new InvalidOperationException(Canary) : Recurse(depth - 1) + 1;

    private sealed class ScriptedTraceException(string trace) : Exception(Canary)
    {
        public override string? StackTrace => trace;
    }

    private sealed class ThrowingTraceException : Exception
    {
        public override string? StackTrace => throw new InvalidOperationException("no trace");
    }
}
