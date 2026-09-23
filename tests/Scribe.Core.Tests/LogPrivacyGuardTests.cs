namespace Scribe.Core.Tests;

/// <summary>
/// The source-level guard for everything that logs near an AI provider, an endpoint, Azure or the user's own
/// words: no log call passes an exception object (behind a cast or not), reads an exception's text (.Message,
/// .StackTrace, inner exceptions) or its Data, renders an object with ToString() or interpolates an exception, and no
/// logging helper is handed an exception's text or its Data, or forwards an exception as a value. Failures reach the
/// log as <c>FailureShape</c> or <c>CleanupFailureShape</c> text.
/// <para>
/// Exception text is not safe in these places. .NET 10 appends "(host:port)" to a connection failure, Azure and
/// Entra errors quote accounts, tenants and resource names, and a failed shell launch quotes the whole URI it was
/// given (the AI report's mailto: carries the report). Settings once logged all of them verbatim.
/// </para>
/// </summary>
public sealed class LogPrivacyGuardTests
{
    // The whole app shell (Settings verifies endpoints, keys and Azure sign-in; the tray, quick add and dictation
    // relay cleanup), every Core folder whose code talks to a provider or handles what one returned, and the
    // provider-facing files of Core folders that are otherwise local.
    private static readonly string[] GuardedFolders =
    [
        Path.Combine("src", "Scribe.App"),
        Path.Combine("src", "Scribe.Core", "Cleanup"),
        Path.Combine("src", "Scribe.Core", "Diagnostics"),
        Path.Combine("src", "Scribe.Core", "Feedback"),
        Path.Combine("src", "Scribe.Core", "Settings"),
    ];

    private static readonly string[] GuardedFiles =
    [
        Path.Combine("src", "Scribe.Core", "PostProcessing", "AiDictionarySuggester.cs"),
        Path.Combine("src", "Scribe.Core", "Transcription", "TranscriptionModelInstaller.cs"),
    ];

    [Fact]
    public void No_app_or_provider_facing_core_log_call_passes_an_exception_or_its_text()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        var appCalls = 0;
        var files = GuardedFolders
            .SelectMany(folder => SourceFiles(Path.Combine(root, folder)))
            .Concat(GuardedFiles.Select(file => Path.Combine(root, file)));
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            if (file.Contains(Path.Combine("src", "Scribe.App") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                appCalls += LogCallScanner.Find(source).Count();
            }

            foreach (var offence in LogCallScanner.Check(source))
            {
                offenders.Add($"{Path.GetRelativePath(root, file)}: {offence.Reason}: {offence.Call}");
            }
        }

        Assert.True(appCalls > 100, $"The scanner found only {appCalls} log calls in the app, so it is not reading the source.");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    // Verbatim from the tree before this guard reached the app, one per way an exception or its text reached the
    // log there. The Settings ones carried the endpoint host, Azure names and the AI report.
    [Theory]
    [InlineData("_log.LogWarning(ex, message);")] // SettingsWindow.TryLog and AzureCliInstaller.TryLog
    [InlineData("_log.LogWarning(ex, \"Could not open a mail client for the AI report.\");")] // SettingsWindow
    [InlineData("_log.LogWarning(ex, \"Could not launch a terminal for the Copilot CLI.\");")] // SettingsWindow.Copilot
    [InlineData("_logger?.LogError(ex, \"Quick add failed to save the dictionary entry.\");")] // QuickAddWindow
    [InlineData("TryLog(log => log.LogWarning(ex, \"Idle model release failed.\"));")] // DictationController
    [InlineData("log?.Log(level, ex, message, args);")] // OverlayProcessClient.TryLogTo
    [InlineData("_host.Services.GetRequiredService<ILogger<App>>()\n    .LogError(ex, \"Failed to learn dictionary terms from history.\");")] // App
    [InlineData("log.LogError(args.Exception, \"Unhandled dispatcher exception.\");")] // App
    [InlineData("log.LogCritical(args.ExceptionObject as Exception, \"Unhandled domain exception (terminating={Terminating}).\", args.IsTerminating);")] // App
    [InlineData("_log.LogError(error, \"#{Id} the active microphone stopped unexpectedly.\", _lifecycle.CurrentDictationId);")] // DictationController
    [InlineData("TryLog(ex, $\"Could not reach {endpoint}: {ex.Message}\");")]
    [InlineData("TryLog(LogLevel.Warning, null, \"Failed: {Error}\", ex.ToString());")]
    [InlineData("TryLog(ex, $\"Failed: {ex}\");")]
    [InlineData("var failure = ex; _log.Log(LogLevel.Warning, failure, message);")]
    public void The_guard_catches_the_ways_the_app_shell_logged_exception_text(string call)
    {
        var source = "class C { void M(object args, object message, object level, object endpoint) " +
            "{ try { } catch (System.Exception ex) { System.Exception error = ex; " + call + " } } }";

        Assert.NotEmpty(LogCallScanner.Check(source));
    }

    [Theory]
    [InlineData("_log.LogWarning(\"{Message} ({Failure})\", message, FailureShape.Describe(ex));")]
    [InlineData("TryLog(ex, \"Could not reach the Azure API-key endpoint.\");")]
    [InlineData("TryLog(LogLevel.Warning, ex, \"Overlay command {Command} failed; tearing down for relaunch.\", item.Verb);")]
    [InlineData("TryLog(log => log.LogWarning(\"Idle model release failed ({Failure}).\", FailureShape.Describe(ex)));")]
    [InlineData("log?.Log(level, template, values);")]
    [InlineData("log.LogError(\"Unhandled dispatcher exception: {Failure}\", FailureShape.DescribeWithStack(args.Exception));")]
    [InlineData("TryLogRefusedFlip(decision);")]
    public void The_guard_allows_what_the_app_shell_logs_now(string call)
    {
        var source = "class C { void M(object args, object message, object level, object item, object template, object values, object decision) " +
            "{ try { } catch (System.Exception ex) { " + call + " } } " +
            "private void TryLog(System.Exception ex, string message) { } " +
            "private static void TryLogTo(object log, object level, System.Exception? ex, string message, params object?[] args) { } }";

        Assert.Empty(LogCallScanner.Check(source));
    }

    [Theory]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", (object)ex);")]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", (object?)ex!);")]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", ((object)ex));")]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", ex as object);")]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", (object)(error));")]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", (System.Exception)args.Exception);")]
    [InlineData("_log.LogWarning(\"Failed ({Payload}).\", ex.Data[\"payload\"]);")]
    [InlineData("_log.LogWarning(\"Failed ({Payload}).\", ex?.Data);")]
    [InlineData("_log.LogWarning(\"Failed ({Keys}).\", string.Join(\",\", ex.Data.Keys));")]
    [InlineData("_log.LogWarning(\"Failed ({Payload}).\", ((System.Exception)error).Data[\"payload\"]);")]
    [InlineData("log.LogError(\"Failed ({Payload}).\", args.Exception.Data[\"payload\"]);")]
    [InlineData("TryLog(ex, \"Failed ({Payload}).\", ex.Data[\"payload\"]);")]
    [InlineData("TryLog(LogLevel.Warning, ex, \"Failed ({Failure}).\", ex);")]
    [InlineData("TryLog(LogLevel.Warning, ex, \"Failed ({Failure}).\", (object)error);")]
    public void The_guard_catches_an_exception_behind_a_cast_or_read_through_its_data(string call)
    {
        // A cast hands on the exception itself, rendered in full, and Data holds whatever the thrower attached. A
        // logging helper renders the exception it takes before its template by shape, but forwards the values after
        // the template as they are.
        var source = "class C { void M(object args, object message, object level, object endpoint) " +
            "{ try { } catch (System.Exception ex) { System.Exception error = ex; " + call + " } } }";

        Assert.NotEmpty(LogCallScanner.Check(source));
    }

    [Theory]
    [InlineData("_log.LogWarning(\"Took {Milliseconds} ms.\", (long)elapsed.TotalMilliseconds);")]
    [InlineData("_log.LogWarning(\"Read {Count} rows.\", result.Data.Count);")]
    [InlineData("_log.LogWarning(\"Failed ({Failure}).\", FailureShape.Describe((System.Exception)error));")]
    [InlineData("_log.LogDebug(\"Could not show the notice ({Error}).\", ex.GetType().Name);")]
    [InlineData("_log.LogWarning(\"Used {Mode}.\", (object)mode ?? \"none\");")]
    [InlineData("TryLog(LogLevel.Warning, ex, \"Failed ({Failure}).\", FailureShape.Describe(ex));")]
    public void The_guard_leaves_casts_and_data_that_are_not_an_exceptions_alone(string call)
    {
        var source = "class C { void M(object elapsed, object result, object mode) " +
            "{ try { } catch (System.Exception ex) { System.Exception error = ex; " + call + " } } }";

        Assert.Empty(LogCallScanner.Check(source));
    }

    [Theory]
    [InlineData("(object)ex", "ex")]
    [InlineData("((object?)ex!)", "ex")]
    [InlineData("ex as object", "ex")]
    [InlineData("(object)(ex)", "ex")]
    [InlineData("(long)elapsed.TotalMilliseconds", "elapsed.TotalMilliseconds")]
    [InlineData("(a) + (b)", "(a) + (b)")]
    [InlineData("FailureShape.Describe(ex)", "FailureShape.Describe(ex)")]
    public void Unwrapping_takes_off_casts_and_enclosing_parentheses_only(string expression, string core)
    {
        Assert.Equal(core, LogCallScanner.Unwrap(expression));
    }

    [Fact]
    public void A_logging_helpers_declaration_is_not_read_as_a_call()
    {
        const string Source = "class C { private void TryLog(System.Exception ex, string message) { } " +
            "private static void TryLogTo(object log, System.Exception? ex, string message) { } }";

        Assert.Empty(LogCallScanner.FindHelperCalls(Source));
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) ||
            file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase);
    }
}
