namespace Scribe.Core.Tests;

public sealed class OverlayLogSourceTests
{
    [Fact]
    public void Overlay_logging_queues_from_the_caller_thread()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var write = MethodBody(source, "public static void Write");

        Assert.Contains("Queue.Writer.TryWrite", write, StringComparison.Ordinal);
        Assert.DoesNotContain("new FileStream", write, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", write, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_warning_and_error_lines_are_prompt_writes()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var write = MethodBody(source, "public static void Write");
        var prompt = MethodBody(source, "private static void WritePromptLine");
        var requires = MethodBody(source, "private static bool RequiresPromptWrite");

        Assert.Contains("RequiresPromptWrite(level)", write, StringComparison.Ordinal);
        Assert.Contains("level is \"Warning\" or \"Error\" or \"Critical\"", requires, StringComparison.Ordinal);
        Assert.Contains("Flush(timeout)", prompt, StringComparison.Ordinal);
        Assert.Contains("AppendWithRetry(Path", prompt, StringComparison.Ordinal);
        Assert.Contains("Monitor.TryEnter(WriteGate, remaining)", prompt, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _dropped)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_error_keeps_stack_frames_but_not_exception_messages()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var error = MethodBody(source, "public static void Error");
        var format = MethodBody(source, "private static string FormatExceptionShape");
        var frames = MethodBody(source, "private static int AppendStackFrames");

        Assert.Contains("FormatExceptionShape(ex)", error, StringComparison.Ordinal);
        Assert.Contains("try", error, StringComparison.Ordinal);
        Assert.Contains("SafeExceptionType(ex)", format, StringComparison.Ordinal);
        Assert.Contains("ex.HResult", format, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", format, StringComparison.Ordinal);
        Assert.Contains("StartsWith(\"at \", StringComparison.Ordinal)", frames, StringComparison.Ordinal);
        Assert.Contains("stackTrace.Split('\\n')", frames, StringComparison.Ordinal);
        Assert.Contains("TrimEnd('\\r')", frames, StringComparison.Ordinal);
        Assert.Contains("MaxExceptionFrames", format + frames, StringComparison.Ordinal);
        Assert.Contains("MaxExceptionFrameChars", frames, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", frames, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_flush_and_writer_emit_pending_drop_notices()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var flush = MethodBody(source, "internal static bool Flush");
        var writer = MethodBody(source, "private static async Task RunWriter");
        var notice = MethodBody(source, "private static void WritePendingDropNotice");

        Assert.Contains("WritePendingDropNotice(deadline)", flush, StringComparison.Ordinal);
        Assert.Contains("WritePendingDropNotice(Environment.TickCount64 + FlushTimeoutMs)", writer, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _dropped, 0)", notice, StringComparison.Ordinal);
        Assert.Contains("DropNotice(dropped)", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_log_calls_never_pass_exception_messages()
    {
        foreach (var file in Directory.EnumerateFiles(RepoFile("src", "Scribe.Overlay"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var line in source.Split('\n'))
            {
                if (line.Contains("OverlayLog.", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(".Message", line, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Overlay_logging_keeps_the_share_retry_append_contract_on_the_writer_thread()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var append = MethodBody(source, "private static bool AppendWithRetry");

        Assert.Contains("FileShare.ReadWrite", append, StringComparison.Ordinal);
        Assert.Contains("catch (IOException)", append, StringComparison.Ordinal);
        Assert.Contains("catch (UnauthorizedAccessException)", append, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"{signature} has no body.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} body was not closed.");
    }

    private static string RepoFile(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine([root!.FullName, .. parts]);
    }
}
