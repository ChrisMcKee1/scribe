using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// Stream PL: while the pill is on and dictation is not paused, the idle deadline trims the overlay helper instead of ending
/// it, the shell warms it on the resume and after settings are applied, and a launch waits up to 30 s for the helper's
/// pipe. The client and the shell cannot run in a test, so this pins their source; the decisions themselves are Core's
/// (OverlayHelperLifetimeTests, OverlayWarmupTests).
/// </summary>
public sealed class OverlayIdleTrimSourceTests
{
    [Fact]
    public void The_keep_warm_period_and_the_resident_flag_are_pushed_together_and_applied_together()
    {
        var controller = Code("src", "Scribe.App", "Overlay", "IOverlayController.cs");
        Assert.Contains("void SetKeepWarm(int minutes, bool keepResident);", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("void SetKeepWarm(int minutes);", controller, StringComparison.Ordinal);

        // One object for the pair, so the consumer can never apply one value without the other.
        var client = Client();
        var push = Body(client, "public void SetKeepWarm(int minutes, bool keepResident)");
        Assert.Contains("var setting = new KeepWarmSetting(minutes, keepResident);", push, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _keepWarm, setting) != setting", push, StringComparison.Ordinal);
        Assert.Contains("TryAdd(Command.KeepWarmChanged);", push, StringComparison.Ordinal);
        Assert.Contains("ApplyKeepWarm(Volatile.Read(ref _keepWarm));", client, StringComparison.Ordinal);

        var apply = Body(client, "private void ApplyKeepWarm(KeepWarmSetting? setting)");
        Assert.Contains("_lifetime.SetIdlePeriodMs(idleMs);", apply, StringComparison.Ordinal);
        Assert.Contains("_lifetime.SetKeepResident(setting.KeepResident);", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trim_hands_the_helper_s_working_set_back_and_keeps_it_running()
    {
        var client = Client();
        var due = Body(client, "private void RunDueWork()");
        Assert.Matches(new Regex(@"case OverlayDueWork\.Trim:\s*TrimHelper\(\);\s*break;"), due);

        var trim = Body(client, "private void TrimHelper()");
        Assert.Contains("HelperWorkingSet.TryTrim(process, _log, out var beforeBytes, out var afterBytes)", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("EndHelper(", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("KillProcess(", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("DiscardLostHelper(", trim, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"""Overlay helper idle for \{IdleMinutes\} minutes: working set trimmed from \{BeforeMb\} MB to \{AfterMb\} MB \(ok \{Trimmed\}\); ""\s*\+\s*""it stays running so the next pill shows at once\."""),
            trim);
        Assert.Contains("LogLevel.Information", trim, StringComparison.Ordinal);

        // -1 for both sizes empties the working set; the client's own Process handle has the access.
        Assert.Contains(
            "SetProcessWorkingSetSizeEx(process.Handle, new IntPtr(-1), new IntPtr(-1), 0)",
            Body(client, "public static bool TryTrim(Process process, ILogger? log, out long beforeBytes, out long afterBytes)"),
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\[DllImport\(""kernel32\.dll"", SetLastError = true\)\]\s*\[return: MarshalAs\(UnmanagedType\.Bool\)\]\s*private static extern bool SetProcessWorkingSetSizeEx\("),
            client);
    }

    [Fact]
    public void A_launch_waits_up_to_30_seconds_for_the_pipe_and_says_how_long_it_took()
    {
        var client = Client();
        Assert.Contains("private const int ConnectTimeoutMs = 30000;", client, StringComparison.Ordinal);
        Assert.Contains("\"Overlay process launched pid={Pid} pipe={Pipe} exe={Exe} after {LaunchMs} ms\"", client, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_pushes_the_resident_flag_Core_decides_everywhere_it_pushes_the_keep_warm_period()
    {
        var shell = Shell();
        Assert.Equal(3, Regex.Matches(shell, Regex.Escape("SetKeepWarm(")).Count);
        Assert.Equal(3, Regex.Matches(shell, Regex.Escape("OverlayWarmup.KeepResident(")).Count);

        // At startup dictation is never paused: nothing restores a pause.
        Assert.Matches(
            new Regex(
                @"_overlay\.SetKeepWarm\(\s*_controller\.CurrentSettings\.ReleaseModelsAfterIdleMinutes,\s*OverlayWarmup\.KeepResident\(_controller\.CurrentSettings\.ShowOverlay, paused: false\)\);"),
            shell);

        // Each render pushes for the state it renders, so a pause pushes false ahead of its release, and a resume true.
        var render = Body(shell, "private void RenderDictationState(DictationStateChange change)");
        var push = Regex.Match(
            render,
            @"_overlay\?\.SetKeepWarm\(\s*settings\.ReleaseModelsAfterIdleMinutes,\s*OverlayWarmup\.KeepResident\(settings\.ShowOverlay, state == DictationState\.Paused\)\);");
        Assert.True(push.Success, "The render must push OverlayWarmup.KeepResident(settings.ShowOverlay, state == DictationState.Paused).");
        var release = render.IndexOf("_overlay?.ReleaseWhenIdle();", StringComparison.Ordinal);
        Assert.True(release > push.Index, "A pause must push the resident flag before it asks for the release.");

        // The settings delegate reads the paused state from the state rendered last, for the push and the warmup alike.
        Assert.Matches(
            new Regex(
                @"var paused = _lastRenderedState == DictationState\.Paused;\s*_overlay\?\.SetKeepWarm\(\s*settings\.ReleaseModelsAfterIdleMinutes,\s*OverlayWarmup\.KeepResident\(settings\.ShowOverlay, paused\)\);"),
            SettingsDelegate(shell));
    }

    [Fact]
    public void The_shell_warms_the_helper_on_the_resume_and_after_settings_are_applied_as_Core_decides()
    {
        var shell = Shell();
        var render = Body(shell, "private void RenderDictationState(DictationStateChange change)");
        Assert.Matches(new Regex(@"var previous = _lastRenderedState;\s*_lastRenderedState = state;"), render);
        Assert.Matches(
            new Regex(
                @"if \(OverlayWarmup\.AfterRender\(previous == DictationState\.Paused, state == DictationState\.Paused, overlayEnabled\)\)\s*\{\s*_overlay\?\.Warmup\(\);\s*\}"),
            render);

        Assert.Matches(
            new Regex(
                @"_overlay\?\.SetPosition\(settings\.OverlayPosition\);\s*if \(OverlayWarmup\.AfterSettingsApplied\(settings\.ShowOverlay, paused\)\)\s*\{\s*_overlay\?\.Warmup\(\);\s*\}"),
            SettingsDelegate(shell));
    }

    [Fact]
    public void No_comment_still_says_an_idle_helper_is_ended_while_the_pill_is_on()
    {
        var shell = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "App.xaml.cs")).ReplaceLineEndings("\n");
        Assert.DoesNotContain("idle helper (~100 MB)", shell, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"A warmed helper left unused is suspended after the\s*//\s*keep-warm period"), shell);

        var controller = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Overlay", "IOverlayController.cs"));
        Assert.DoesNotContain("a helper left idle for this many minutes is ended to reclaim its", controller, StringComparison.Ordinal);
    }

    private static string Client() => Code("src", "Scribe.App", "Overlay", "OverlayProcessClient.cs");

    private static string Shell() => Code("src", "Scribe.App", "App.xaml.cs");

    // The settings delegate the shell hands the Settings window, from its first statement to its return.
    private static string SettingsDelegate(string shell)
    {
        var start = shell.IndexOf("_settingsWrites?.NoteExternalApply();", StringComparison.Ordinal);
        Assert.True(start >= 0, "The settings delegate was not found.");
        var end = shell.IndexOf("return applying;", start, StringComparison.Ordinal);
        Assert.True(end > start, "The settings delegate has no return.");
        return shell[start..end];
    }

    private static string Code(params string[] path) =>
        Regex.Replace(File.ReadAllText(Path.Combine([RepositoryRoot(), .. path])), @"//[^\n]*", string.Empty).ReplaceLineEndings("\n");

    // The text of a member from its signature to the brace that closes its body.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = code.IndexOf('{', code.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
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
}
