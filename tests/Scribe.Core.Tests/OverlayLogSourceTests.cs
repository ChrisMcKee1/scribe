namespace Scribe.Core.Tests;

public sealed class OverlayLogSourceTests
{
    [Fact]
    public void Overlay_logging_queues_from_the_caller_thread()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var write = MethodBody(source, "public static void Write");

        Assert.Contains("Queue.Enqueue(line)", write, StringComparison.Ordinal);
        Assert.Contains("Signal.Release()", write, StringComparison.Ordinal);
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
        Assert.Contains("PriorityQueue.Enqueue(entry)", prompt, StringComparison.Ordinal);
        Assert.Contains("entry.Completed.Wait(timeoutMs)", prompt, StringComparison.Ordinal);
        Assert.Contains("StartPromptHelper(entry)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendWithRetry", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor.TryEnter", prompt, StringComparison.Ordinal);
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
        var writer = MethodBody(source, "private static void RunWriter");
        var notice = MethodBody(source, "private static void WritePendingDropNotice");

        Assert.Contains("WritePendingDropNotice(deadline)", flush, StringComparison.Ordinal);
        Assert.Contains("WritePendingDropNotice(Environment.TickCount64 + FlushTimeoutMs)", writer, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _dropped, 0)", notice, StringComparison.Ordinal);
        Assert.Contains("DropNotice(dropped)", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_writer_drains_priority_before_ordinary_lines()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var writer = MethodBody(source, "private static void RunWriter");

        var priority = writer.IndexOf("DrainPriority();", StringComparison.Ordinal);
        var ordinary = writer.IndexOf("DrainOrdinaryBatch();", StringComparison.Ordinal);
        Assert.True(priority >= 0, "priority drain was not found");
        Assert.True(ordinary >= 0, "ordinary drain was not found");
        Assert.True(priority < ordinary, "priority lines must be drained before ordinary lines");
    }

    [Fact]
    public void Overlay_prompt_fallback_uses_one_helper_thread_and_not_the_caller()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var start = MethodBody(source, "private static void StartPromptHelper");
        var run = MethodBody(source, "private static void RunPromptHelper");

        Assert.Contains("Interlocked.CompareExchange(ref _helperRunning, 1, 0)", start, StringComparison.Ordinal);
        Assert.Contains("new Thread(() => RunPromptHelper(entry))", start, StringComparison.Ordinal);
        Assert.Contains("WriteSingleLine(entry.Line", run, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _helperRunning, 0)", run, StringComparison.Ordinal);
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
