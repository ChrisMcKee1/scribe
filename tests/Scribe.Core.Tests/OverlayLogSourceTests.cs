namespace Scribe.Core.Tests;

public sealed class OverlayLogSourceTests
{
    [Fact]
    public void Overlay_logging_uses_the_synchronous_share_retry_append_path()
    {
        var source = File.ReadAllText(RepoFile("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"));
        var write = MethodBody(source, "public static void Write");
        var append = MethodBody(source, "private static void AppendLine");

        Assert.Contains("AppendLine(line)", write, StringComparison.Ordinal);
        Assert.Contains("new FileStream", append, StringComparison.Ordinal);
        Assert.Contains("FileShare.ReadWrite", append, StringComparison.Ordinal);
        Assert.Contains("catch (IOException)", append, StringComparison.Ordinal);
        Assert.Contains("catch (UnauthorizedAccessException)", append, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(15)", append, StringComparison.Ordinal);
        Assert.DoesNotContain("Channel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PriorityQueue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessExit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Flush(", source, StringComparison.Ordinal);
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
