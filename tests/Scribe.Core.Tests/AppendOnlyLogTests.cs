using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Overlay.Logging;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-O-02 (<see cref="PerfFlags.AppendOnlyLog"/>): the app and the overlay helper it launches append to the shared daily
/// log through handles that may only append, so neither can write over a line the other appended between its open and its
/// write. One mode for the pair, decided by the app for each helper it launches and passed as a launch argument; with the
/// flag off both keep today's <see cref="FileMode.Append"/> stream. The overlay's writer is compiled in from its source.
/// </summary>
public sealed class AppendOnlyLogTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 21);
    private static readonly DateTime Noon = Day.ToDateTime(new TimeOnly(12, 0));
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("scribe-appendonly-");

    public void Dispose()
    {
        try { _folder.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string LogsDir => Path.Combine(_folder.FullName, "logs");

    private string DayPath => ScribeLogFiles.PathFor(LogsDir, Day);

    // ---- The decision -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "0.5.1+77b22af", "0.5.1+77b22af", true, AppendOnlyLaunchDecision.AppendOnly)]
    [InlineData(false, "0.5.1+77b22af", "0.5.1+77b22af", true, AppendOnlyLaunchDecision.NotRequested)]
    [InlineData(true, "0.5.1+77b22af", "0.5.1+77b22af", false, AppendOnlyLaunchDecision.HelperNotCapable)]
    [InlineData(true, "0.5.1+77b22af", "0.5.1+f88db0a", true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, "0.5.1+77b22af", "0.5.0+77b22af", true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, "0.5.1+abc", "0.5.1+ABC", true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, "0.5.1+77b22af", null, true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, null, "0.5.1+77b22af", true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, null, null, true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, "", "", true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, " ", " ", true, AppendOnlyLaunchDecision.OtherBuild)]
    [InlineData(true, null, null, false, AppendOnlyLaunchDecision.HelperNotCapable)]
    public void The_pair_appends_only_when_asked_for_a_helper_of_this_build_that_declares_it_follows_the_argument(
        bool requested, string? appVersion, string? helperVersion, bool capable, AppendOnlyLaunchDecision decision) =>
        Assert.Equal(decision, AppendOnlyLogMode.Decide(requested, appVersion, new HelperPayload(helperVersion, capable)));

    [Fact]
    public void A_build_s_version_is_its_informational_version()
    {
        var core = typeof(PerfFlags).Assembly;
        var informational = core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational));
        Assert.Equal(informational, AppendOnlyLogMode.ReadVersion(core.Location));
    }

    [Fact]
    public void The_helper_s_payload_is_read_from_its_assembly_beside_the_executable_and_is_nothing_when_it_cannot_be()
    {
        var legacy = HelperBuiltFrom(typeof(PerfFlags).Assembly.Location, "legacy");
        var capable = HelperBuiltFrom(CapableHelperAssembly(), "capable");
        var notAnAssembly = Path.Combine(_folder.FullName, "not-an-assembly.dll");
        File.WriteAllText(notAnAssembly, "not a PE image");
        var appVersion = AppendOnlyLogMode.ReadVersion(typeof(PerfFlags).Assembly.Location);

        Assert.Equal(new HelperPayload(appVersion, DeclaresCapability: false), AppendOnlyLogMode.ReadHelperPayload(legacy));
        Assert.Equal(new HelperPayload(appVersion, DeclaresCapability: true), AppendOnlyLogMode.ReadHelperPayload(capable));
        Assert.Equal(appVersion, AppendOnlyLogMode.ReadOverlayVersion(legacy));
        Assert.Equal(HelperPayload.Unreadable, AppendOnlyLogMode.ReadHelperPayload(Path.Combine(_folder.FullName, "missing", "Scribe.Overlay.exe")));
        Assert.Equal(HelperPayload.Unreadable, AppendOnlyLogMode.ReadHelperPayload(null));
        Assert.Equal(HelperPayload.Unreadable, AppendOnlyLogMode.ReadHelperPayload(" "));
        Assert.Equal(HelperPayload.Unreadable, AppendOnlyLogMode.ReadHelperPayload("Scribe.Overlay.exe"));
        Assert.Null(AppendOnlyLogMode.ReadVersion(Path.Combine(_folder.FullName, "missing.dll")));
        Assert.Null(AppendOnlyLogMode.ReadVersion(notAnAssembly));
        Assert.Null(AppendOnlyLogMode.ReadVersion(null));
    }

    public static TheoryData<string, string?, string?, string?, AppendOnlyLaunchDecision> SyntheticHelpers() => new()
    {
        // name, informational version (null: the app's), capability key, capability value, decision
        { "capable", null, AppendOnlyLogMode.CapabilityKey, "1", AppendOnlyLaunchDecision.AppendOnly },
        { "capable-other-version", "0.0.1+ffffff", AppendOnlyLogMode.CapabilityKey, "1", AppendOnlyLaunchDecision.OtherBuild },
        { "capable-no-version", "", AppendOnlyLogMode.CapabilityKey, "1", AppendOnlyLaunchDecision.OtherBuild },
        { "declares-zero", null, AppendOnlyLogMode.CapabilityKey, "0", AppendOnlyLaunchDecision.HelperNotCapable },
        { "declares-two", null, AppendOnlyLogMode.CapabilityKey, "2", AppendOnlyLaunchDecision.HelperNotCapable },
        { "other-case-key", null, "scribe.sharedlogappendonly", "1", AppendOnlyLaunchDecision.HelperNotCapable },
        { "other-key", null, "Scribe.SomethingElse", "1", AppendOnlyLaunchDecision.HelperNotCapable },
        { "no-metadata", null, null, null, AppendOnlyLaunchDecision.HelperNotCapable },
    };

    [Theory]
    [MemberData(nameof(SyntheticHelpers))]
    public void Only_the_exact_capability_entry_with_the_app_s_version_turns_the_pair_append_only(
        string name, string? version, string? key, string? value, AppendOnlyLaunchDecision decision)
    {
        var appVersion = AppendOnlyLogMode.ReadVersion(typeof(PerfFlags).Assembly.Location);
        var helper = SyntheticHelper(name, version is null ? appVersion : version.Length == 0 ? null : version, key, value);
        var mode = new AppendOnlyLogMode(requested: true, appVersion);

        Assert.Equal(decision == AppendOnlyLaunchDecision.AppendOnly, mode.DecideForLaunch(helper));
        Assert.Equal(decision, mode.LastDecision);
        Assert.Equal(decision == AppendOnlyLaunchDecision.AppendOnly, mode.AppendOnly);
    }

    [Fact]
    public void Every_launch_decides_for_the_helper_it_starts_and_the_app_s_writer_follows_before_it_starts()
    {
        var appVersion = AppendOnlyLogMode.ReadVersion(typeof(PerfFlags).Assembly.Location);
        var capable = HelperBuiltFrom(CapableHelperAssembly(), "capable");

        // A build of the same informational version that does not declare the capability: a stale output from before it.
        var legacySameVersion = HelperBuiltFrom(typeof(PerfFlags).Assembly.Location, "legacy-same-version");
        var anotherBuild = HelperBuiltFrom(typeof(ILogger).Assembly.Location, "another-build");
        var unreadable = Path.Combine(_folder.FullName, "unreadable", "Scribe.Overlay.exe");
        var mode = new AppendOnlyLogMode(requested: true, appVersion);

        // Until the first launch nothing else writes the file, so the app's writer starts the old way.
        Assert.True(mode.Requested);
        Assert.False(mode.AppendOnly);
        Assert.Equal(AppendOnlyLaunchDecision.None, mode.LastDecision);

        Assert.True(mode.DecideForLaunch(capable));
        Assert.True(mode.AppendOnly);

        // A relaunch decides again, for whatever helper output it starts.
        Assert.True(mode.DecideForLaunch(capable));
        Assert.True(mode.AppendOnly);
        Assert.False(mode.DecideForLaunch(legacySameVersion));
        Assert.Equal(AppendOnlyLaunchDecision.HelperNotCapable, mode.LastDecision);
        Assert.False(mode.AppendOnly);
        Assert.True(mode.DecideForLaunch(capable));
        Assert.False(mode.DecideForLaunch(anotherBuild));
        Assert.False(mode.AppendOnly);
        Assert.False(mode.DecideForLaunch(unreadable));
        Assert.False(mode.AppendOnly);
        Assert.True(mode.DecideForLaunch(capable));
        Assert.True(mode.AppendOnly);

        var off = new AppendOnlyLogMode(requested: false, appVersion);
        Assert.False(off.DecideForLaunch(capable));
        Assert.Equal(AppendOnlyLaunchDecision.NotRequested, off.LastDecision);
        Assert.False(off.AppendOnly);

        var noVersion = new AppendOnlyLogMode(requested: true, appVersion: null);
        Assert.False(noVersion.DecideForLaunch(capable));
        Assert.Equal(AppendOnlyLaunchDecision.OtherBuild, noVersion.LastDecision);
        Assert.False(noVersion.AppendOnly);
    }

    [Fact]
    public void The_overlay_build_declares_the_capability_with_the_argument_and_the_app_reads_it_from_the_helper_it_starts()
    {
        var overlay = Source("src", "Scribe.Overlay", "Scribe.Overlay.csproj");
        Assert.Contains(
            $"<AssemblyMetadata Include=\"{AppendOnlyLogMode.CapabilityKey}\" Value=\"{AppendOnlyLogMode.CapabilityValue}\" />",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains("AppendOnlyFile.LaunchArgument", Source("src", "Scribe.Overlay", "Logging", "OverlayLog.cs"), StringComparison.Ordinal);

        // Where this tree has built the overlay, every output of this build (the app's informational version) declares it.
        var appVersion = AppendOnlyLogMode.ReadVersion(typeof(PerfFlags).Assembly.Location);
        var bin = Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay", "bin");
        var outputs = Directory.Exists(bin) ? Directory.GetFiles(bin, "Scribe.Overlay.dll", SearchOption.AllDirectories) : [];
        foreach (var payload in outputs.Select(output => AppendOnlyLogMode.ReadHelperPayload(Path.ChangeExtension(output, ".exe"))))
        {
            if (payload.InformationalVersion == appVersion)
            {
                Assert.True(payload.DeclaresCapability);
            }
        }
    }

    // ---- The switch retires the writes of the other way (DATA-IMPL-A-01) ---------------------------------------------

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_switch_waits_for_a_write_of_the_other_way_held_between_its_open_and_its_write(bool from, bool to)
    {
        var mode = AppendOnlyLogMode.Fixed(from);
        var file = OpenFile(mode);
        file.Write([Record("existing")]);
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        file.BetweenOpenAndWrite = () =>
        {
            file.BetweenOpenAndWrite = null;
            opened.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var held = Task.Run(() => file.Write([Record("held app line")]));
        Assert.True(opened.Wait(TimeSpan.FromSeconds(30)));
        var switching = Task.Run(() => mode.SwitchTo(to, TimeSpan.FromSeconds(30)));

        // The helper cannot start while the app's write the other way is open: the switch has not returned.
        Assert.NotSame(switching, await Task.WhenAny(switching, Task.Delay(300)));
        release.Set();
        Assert.Equal(to, await switching.WaitAsync(TimeSpan.FromSeconds(30)));
        await held.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(to, mode.AppendOnly);

        // Only now does the helper start and append its first line the way the switch decided; the app goes on the same way.
        OverlayLog.AppendLineForTests(DayPath, "helper line", appendOnly: to);
        file.Write([Record("app line after")]);

        Assert.Equal(["existing", "held app line", "helper line", "app line after"], File.ReadAllLines(DayPath));
    }

    [Fact]
    public async Task A_write_the_old_way_that_does_not_end_in_time_keeps_the_pair_on_today_s_way()
    {
        var appVersion = AppendOnlyLogMode.ReadVersion(typeof(PerfFlags).Assembly.Location);
        var mode = new AppendOnlyLogMode(requested: true, appVersion);
        var capable = HelperBuiltFrom(CapableHelperAssembly(), "capable");
        var file = OpenFile(mode);
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        file.BetweenOpenAndWrite = () =>
        {
            file.BetweenOpenAndWrite = null;
            opened.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var held = Task.Run(() => file.Write([Record("held app line")]));
        Assert.True(opened.Wait(TimeSpan.FromSeconds(30)));

        // Bounded: the launcher gives up after the handover timeout and both keep today's way.
        Assert.False(mode.SwitchTo(true, TimeSpan.FromMilliseconds(100)));
        Assert.False(mode.AppendOnly);
        Assert.False(await Task.Run(() => mode.DecideForLaunch(capable)).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(AppendOnlyLaunchDecision.WriteInProgress, mode.LastDecision);
        Assert.False(mode.AppendOnly);

        release.Set();
        await held.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(mode.DecideForLaunch(capable));
        Assert.True(mode.AppendOnly);
    }

    [Fact]
    public void A_write_that_read_the_mode_just_before_a_switch_takes_the_new_way_when_it_announces_itself()
    {
        var mode = AppendOnlyLogMode.Fixed(false);
        var switched = false;
        mode.AfterModeRead = () =>
        {
            mode.AfterModeRead = null;

            // The switch lands after the write read "today's way" and before it announced itself: it sees no write in
            // progress and returns at once, so a helper could start now.
            var switching = new Thread(() => switched = mode.SwitchTo(true, TimeSpan.FromSeconds(30)));
            switching.Start();
            Assert.True(switching.Join(TimeSpan.FromSeconds(30)));
        };

        using var write = mode.BeginWrite();

        Assert.True(switched);
        Assert.True(mode.AppendOnly);
        Assert.True(write.AppendOnly);
    }

    [Fact]
    public async Task Every_line_survives_switches_back_and_forth_while_the_app_writes_all_the_while()
    {
        var mode = AppendOnlyLogMode.Fixed(false);
        var file = OpenFile(mode, retryDelay: TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var written = 0;
        var app = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                file.Write([Record($"app {written:D6}")]);
                written++;
            }
        });

        var helperLines = new List<string>();
        for (var round = 0; round < 100; round++)
        {
            Assert.True(mode.SwitchTo(true, TimeSpan.FromSeconds(10)));
            var line = $"helper {round:D3}";
            OverlayLog.AppendLineForTests(DayPath, line, appendOnly: true);
            helperLines.Add(line);
            Assert.False(mode.SwitchTo(false, TimeSpan.FromSeconds(10)));
        }

        stop.Cancel();
        await app.WaitAsync(TimeSpan.FromSeconds(30));
        var lines = File.ReadAllLines(DayPath);
        Assert.All(helperLines, line => Assert.Single(lines, candidate => candidate == line));
        Assert.Equal(written, lines.Count(line => line.StartsWith("app ", StringComparison.Ordinal)));
        Assert.Equal(lines.Length, written + helperLines.Count);
    }
    [Fact]
    public void A_mode_set_for_a_tool_says_what_it_was_given()
    {
        Assert.True(AppendOnlyLogMode.Fixed(true).AppendOnly);
        Assert.False(AppendOnlyLogMode.Fixed(false).AppendOnly);
        Assert.Equal(AppendOnlyFile.LaunchArgument, AppendOnlyLogMode.LaunchArgument);
    }

    // ---- What a handle that may only append guarantees --------------------------------------------------------------

    [Fact]
    public void A_handle_that_may_only_append_never_writes_over_a_line_appended_after_it_opened()
    {
        var path = Path.Combine(_folder.FullName, "handles.log");
        File.WriteAllText(path, "existing\r\n");

        // Both open before either writes, and the one opened later writes first: the other still lands after it.
        using (var first = AppendOnlyFile.Open(path))
        using (var second = AppendOnlyFile.Open(path))
        {
            second.Write(Encoding.UTF8.GetBytes("second\r\n"));
            first.Write(Encoding.UTF8.GetBytes("first\r\n"));
        }

        Assert.Equal("existing\r\nsecond\r\nfirst\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Opening_to_append_creates_a_missing_file_and_shares_it_as_today_s_writers_do()
    {
        var path = Path.Combine(_folder.FullName, "created.log");

        using (var appendOnly = AppendOnlyFile.Open(path))
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var otherWriter = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0))
        {
            Assert.True(appendOnly.CanWrite);

            // .NET 10 reports such a stream as readable too (see AppendOnlyFile); the handle Windows gave it is not.
            Assert.Throws<UnauthorizedAccessException>(() => appendOnly.ReadByte());
            appendOnly.Write("one\r\n"u8);
            Assert.Equal(5, reader.Length);
        }

        // A reader that shares writes (the diagnostics bundle's copy) is already open when the writer opens.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var appendOnly = AppendOnlyFile.Open(path))
        {
            appendOnly.Write("two\r\n"u8);
        }

        Assert.Equal("one\r\ntwo\r\n", File.ReadAllText(path));

        // A holder that refuses writers keeps it out, as it keeps today's stream out.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => AppendOnlyFile.Open(path).Dispose());
            Assert.ThrowsAny<IOException>(() =>
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0).Dispose());
        }
    }

    [Fact]
    public void An_app_left_the_old_way_can_still_write_over_the_helper_s_line_so_the_pair_never_mixes()
    {
        Directory.CreateDirectory(LogsDir);
        File.WriteAllText(DayPath, "existing\r\n");

        // The statement today's DailyLogFile opens with, held across the helper's append-only line.
        using (var appOldWay = new FileStream(DayPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0))
        {
            OverlayLog.AppendLineForTests(DayPath, "helper line", appendOnly: true);
            appOldWay.Write("app line\r\n"u8);
        }

        var text = File.ReadAllText(DayPath);
        Assert.StartsWith("existing\r\napp line\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("helper line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_helper_left_the_old_way_can_still_write_over_the_app_s_line_so_the_pair_never_mixes()
    {
        var file = OpenFile(appendOnly: true);
        file.Write([Record("existing")]);

        // The statement today's OverlayLog opens a short line with, held across the app's append-only batch.
        using (var helperOldWay = new FileStream(DayPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0))
        {
            file.Write([Record("app line")]);
            helperOldWay.Write("helper line\r\n"u8);
        }

        var text = File.ReadAllText(DayPath);
        Assert.StartsWith("existing\r\nhelper line\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("app line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void With_both_appending_only_neither_can_write_over_the_other_in_either_order()
    {
        var file = OpenFile(appendOnly: true);
        file.Write([Record("existing")]);

        using (var helper = AppendOnlyFile.Open(DayPath))
        {
            file.Write([Record("app line")]);
            helper.Write("helper line\r\n"u8);
        }

        using (var app = AppendOnlyFile.Open(DayPath))
        {
            OverlayLog.AppendLineForTests(DayPath, "second helper line", appendOnly: true);
            app.Write("second app line\r\n"u8);
        }

        Assert.Equal(
            ["existing", "app line", "helper line", "second helper line", "second app line"], File.ReadAllLines(DayPath));
    }

    // ---- Both writers at once ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_app_s_writer_and_the_helper_s_appending_at_once_keep_every_line_once_and_whole()
    {
        const int Count = 2000;
        var file = OpenFile(appendOnly: true, retryDelay: TimeSpan.FromMilliseconds(1));
        using var start = new Barrier(2);

        var app = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < Count; i++)
            {
                file.Write([new LogRecord(Noon, LogLevel.Information, ChildLine.Format("app", i))]);
            }
        });
        var helper = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < Count; i++)
            {
                OverlayLog.AppendLineForTests(DayPath, ChildLine.Format("helper", i), appendOnly: true);
            }
        });

        await Task.WhenAll(app, helper).WaitAsync(TimeSpan.FromSeconds(60));
        AssertEveryLineOnceAndWhole(File.ReadAllLines(DayPath), ("app", Count), ("helper", Count));
    }

    [Fact]
    public async Task Two_processes_appending_at_once_through_the_real_writers_keep_every_line_once_and_whole()
    {
        const int Count = 5000;
        Directory.CreateDirectory(LogsDir);
        var eventName = @"Local\Scribe.AppendOnlyLogTests." + Guid.NewGuid().ToString("N");
        using var start = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var children = new List<Process>();
        try
        {
            children.Add(StartChild("app", "app", Count, eventName));
            children.Add(StartChild("overlay", "helper", Count, eventName));
            foreach (var child in children)
            {
                Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            }

            start.Set();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var child in children)
            {
                await child.WaitForExitAsync(deadline.Token);
                Assert.Equal(string.Empty, await child.StandardError.ReadToEndAsync(deadline.Token));
                Assert.Equal(0, child.ExitCode);
            }
        }
        finally
        {
            foreach (var child in children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                child.Dispose();
            }
        }

        AssertEveryLineOnceAndWhole(File.ReadAllLines(DayPath), ("app", Count), ("helper", Count));
    }

    // ---- The day file and its folder --------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_day_file_renamed_or_replaced_between_writes_takes_the_next_lines_as_today_s_writers_do(bool appendOnly)
    {
        var file = OpenFile(appendOnly);
        file.Write([Record("before")]);
        OverlayLog.AppendLineForTests(DayPath, "helper before", appendOnly);

        var moved = DayPath + ".moved";
        File.Move(DayPath, moved);
        file.Write([Record("after the rename")]);
        OverlayLog.AppendLineForTests(DayPath, "helper after the rename", appendOnly);

        Assert.Equal(["before", "helper before"], File.ReadAllLines(moved));
        Assert.Equal(["after the rename", "helper after the rename"], File.ReadAllLines(DayPath));

        File.Delete(DayPath);
        File.WriteAllText(DayPath, "replacement\r\n");
        file.Write([Record("after the replacement")]);
        OverlayLog.AppendLineForTests(DayPath, "helper after the replacement", appendOnly);

        Assert.Equal(["replacement", "after the replacement", "helper after the replacement"], File.ReadAllLines(DayPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_deleted_folder_is_recreated_by_the_app_and_left_to_the_next_day_by_the_helper_as_today(bool appendOnly)
    {
        var file = OpenFile(appendOnly);
        file.Write([Record("first")]);
        Directory.Delete(LogsDir, recursive: true);

        // The helper makes its folder only when its day changes (OverlayLog.Path): its attempts fail, and it gives up.
        OverlayLog.AppendLineForTests(DayPath, "helper while the folder is gone", appendOnly);
        Assert.False(Directory.Exists(LogsDir));

        file.Write([Record("second")]);
        OverlayLog.AppendLineForTests(DayPath, "helper after the app made it again", appendOnly);

        Assert.Equal(["second", "helper after the app made it again"], File.ReadAllLines(DayPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_locked_against_writers_or_made_read_only_is_retried_then_given_up_by_both_writers(bool appendOnly)
    {
        var file = OpenFile(appendOnly);
        using (new FileStream(DayPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
        {
            file.Write([Record("lost while locked")]);
            OverlayLog.AppendLineForTests(DayPath, "helper lost while locked", appendOnly);
        }

        File.SetAttributes(DayPath, FileAttributes.ReadOnly);
        try
        {
            file.Write([Record("lost while read-only")]);
            OverlayLog.AppendLineForTests(DayPath, "helper lost while read-only", appendOnly);
        }
        finally
        {
            File.SetAttributes(DayPath, FileAttributes.Normal);
        }

        file.Write([Record("after")]);
        OverlayLog.AppendLineForTests(DayPath, "helper after", appendOnly);

        Assert.Equal(["after", "helper after"], File.ReadAllLines(DayPath));
    }

    [Fact]
    public void The_diagnostics_bundle_copies_today_s_file_while_a_writer_holds_it_open_to_append()
    {
        var file = OpenFile(appendOnly: true);
        file.Write([Record("a line for the bundle")]);
        var zip = Path.Combine(_folder.FullName, "bundle.zip");

        using (AppendOnlyFile.Open(DayPath))
        {
            var result = DiagnosticsBundle.Create(LogsDir, zip, "report", Day);
            Assert.Equal(1, result.LogFileCount);
        }

        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("logs/" + Path.GetFileName(DayPath))!.Open());
        Assert.Contains("a line for the bundle", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    // ---- The helper's lines -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1021)]
    [InlineData(1022)]
    [InlineData(1023)]
    [InlineData(5000)]
    [InlineData(70000)]
    public void The_helper_writes_the_same_bytes_either_way_for_short_and_long_lines(int length)
    {
        var line = MultibyteLine(length);
        var oldWay = Path.Combine(_folder.FullName, "old.log");
        var appendOnlyWay = Path.Combine(_folder.FullName, "append-only.log");
        File.WriteAllText(oldWay, "existing\r\n");
        File.WriteAllText(appendOnlyWay, "existing\r\n");

        OverlayLog.AppendLineForTests(oldWay, line, appendOnly: false);
        OverlayLog.AppendLineForTests(appendOnlyWay, line, appendOnly: true);

        Assert.Equal(File.ReadAllBytes(oldWay), File.ReadAllBytes(appendOnlyWay));
        Assert.Equal("existing\r\n" + line + Environment.NewLine, File.ReadAllText(appendOnlyWay));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(3000)]
    public void A_line_that_is_not_well_formed_adds_nothing_the_append_only_way(int prefix)
    {
        // OverlayLog.Write swallows what AppendLine throws; the file is not even opened.
        var existing = Path.Combine(_folder.FullName, "existing.log");
        var missing = Path.Combine(_folder.FullName, "missing.log");
        File.WriteAllText(existing, "existing\r\n");
        var line = new string('x', prefix) + "\uD800" + " tail";

        Assert.Throws<EncoderFallbackException>(() => OverlayLog.AppendLineForTests(existing, line, appendOnly: true));
        Assert.Throws<EncoderFallbackException>(() => OverlayLog.AppendLineForTests(missing, line, appendOnly: true));

        Assert.Equal("existing\r\n", File.ReadAllText(existing));
        Assert.False(File.Exists(missing));
    }

    // ---- Where the mode comes from, and where it goes ---------------------------------------------------------------

    [Fact]
    public void The_helper_follows_its_launch_argument_and_never_its_environment()
    {
        var source = Source("src", "Scribe.Overlay", "Logging", "OverlayLog.cs");

        Assert.Contains(
            "private static readonly bool AppendOnly = HasArgument(Environment.GetCommandLineArgs(), AppendOnlyFile.LaunchArgument);",
            source,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, Regex.Escape("GetEnvironmentVariable(")));
        Assert.Contains("GetEnvironmentVariable(\"SCRIBE_DATA_DIR\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SCRIBE_PERF_FLAGS", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PerfFlags.FromEnvironment", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn(", source, StringComparison.Ordinal);
        Assert.Contains("AppendLine(path, line, AppendOnly);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_passes_the_argument_only_to_a_helper_it_decided_for_on_every_launch()
    {
        var client = Source("src", "Scribe.App", "Overlay", "OverlayProcessClient.cs");

        Assert.Single(Regex.Matches(client, Regex.Escape("Process.Start(")));
        Assert.Single(Regex.Matches(client, Regex.Escape("LaunchArgument")));
        string[] order =
        [
            "private LaunchOutcome TryLaunch()",
            "psi.ArgumentList.Add(\"--parent\");",
            "if (_appendMode is { Requested: true } appendMode)",
            "var appendOnly = appendMode.DecideForLaunch(_exePath);",
            "if (appendOnly)",
            "psi.ArgumentList.Add(Scribe.Core.Diagnostics.AppendOnlyLogMode.LaunchArgument);",
            "Process.Start(psi)",
        ];
        var positions = order.Select(text => client.IndexOf(text, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void The_app_s_writer_and_its_overlay_client_share_one_mode_read_from_the_flag()
    {
        var app = Source("src", "Scribe.App", "App.xaml.cs");

        Assert.Single(Regex.Matches(app, Regex.Escape("perfFlags.IsOn(PerfFlags.AppendOnlyLog)")));
        Assert.Contains("new FileLoggerProvider(paths.LogsDir, appendMode: logAppendMode);", app, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"new OverlayProcessClient\([^;]*,\s*logAppendMode\);"), app);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private DailyLogFile OpenFile(bool appendOnly, TimeSpan? retryDelay = null) =>
        DailyLogFile.Open(
            LogsDir, null, long.MaxValue, retryDelay: retryDelay ?? TimeSpan.Zero, clock: () => Noon,
            appendMode: appendOnly ? AppendOnlyLogMode.Fixed(true) : null);

    private DailyLogFile OpenFile(AppendOnlyLogMode mode, TimeSpan? retryDelay = null) =>
        DailyLogFile.Open(
            LogsDir, null, long.MaxValue, retryDelay: retryDelay ?? TimeSpan.Zero, clock: () => Noon, appendMode: mode);

    // A payload that declares the capability and carries this build's informational version: the child writer compiles the
    // overlay's writer and declares it as the overlay does.
    private static string CapableHelperAssembly() => Path.ChangeExtension(ChildExecutable(), ".dll");

    // An assembly named as the helper's, with the given informational version and one metadata entry, made on the spot.
    private string SyntheticHelper(string name, string? informationalVersion, string? key, string? value)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder.FullName, name));
        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("Scribe.Overlay"), typeof(object).Assembly);
        if (informationalVersion is not null)
        {
            builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informationalVersion]));
        }

        if (key is not null)
        {
            builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
                typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, [key, value]));
        }

        builder.DefineDynamicModule("Scribe.Overlay");
        builder.Save(Path.Combine(folder.FullName, "Scribe.Overlay.dll"));
        return Path.Combine(folder.FullName, "Scribe.Overlay.exe");
    }

    private static LogRecord Record(string text) => new(Noon, LogLevel.Information, text);

    // A copy of an assembly named as the helper's, beside a helper executable that need not exist.
    private string HelperBuiltFrom(string assembly, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder.FullName, name));
        File.Copy(assembly, Path.Combine(folder.FullName, "Scribe.Overlay.dll"));
        return Path.Combine(folder.FullName, "Scribe.Overlay.exe");
    }

    // ASCII with two-, three- and four-byte characters mixed in, surrogate pairs never split, so a long line crosses the
    // StreamWriter's 1,024-character pieces inside multibyte characters and pairs.
    private static string MultibyteLine(int length)
    {
        var builder = new StringBuilder(length);
        var i = 0;
        while (builder.Length < length)
        {
            switch (i++ % 5)
            {
                case 0: builder.Append('\u00e9'); break;
                case 1: builder.Append('\u4e2d'); break;
                case 2 when builder.Length + 2 <= length: builder.Append("\U0001F600"); break;
                default: builder.Append((char)('a' + (i % 26))); break;
            }
        }

        return builder.ToString();
    }

    private static void AssertEveryLineOnceAndWhole(string[] lines, params (string Tag, int Count)[] writers)
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (tag, count) in writers)
        {
            for (var i = 0; i < count; i++)
            {
                expected[ChildLine.Format(tag, i)] = 0;
            }
        }

        var unexpected = new List<string>();
        foreach (var line in lines)
        {
            if (expected.TryGetValue(line, out var seen))
            {
                expected[line] = seen + 1;
            }
            else
            {
                unexpected.Add(line);
            }
        }

        var missing = expected.Where(pair => pair.Value == 0).Select(pair => pair.Key).ToList();
        var repeated = expected.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToList();
        Assert.True(
            missing.Count == 0 && repeated.Count == 0 && unexpected.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{missing.Count} line(s) missing, {repeated.Count} repeated and {unexpected.Count} torn or unexpected " +
                $"of {lines.Length}; first missing: {missing.FirstOrDefault()}; first unexpected: {unexpected.FirstOrDefault()}"));
    }

    private Process StartChild(string writer, string tag, int count, string eventName)
    {
        var info = new ProcessStartInfo(ChildExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     writer, "append-only", LogsDir, Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), tag,
                     count.ToString(CultureInfo.InvariantCulture), eventName,
                 })
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("The child writer did not start.");
    }

    // tests\Scribe.LogAppendChild, built beside the tests by their project reference, in the same configuration.
    private static string ChildExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}Scribe.Core.Tests{separator}bin{separator}";
        var index = baseDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(index >= 0, "The tests do not run from their bin folder: " + baseDirectory);

        var testsFolder = baseDirectory[..index];
        var rest = baseDirectory[(index + marker.Length)..];
        var mapped = Path.Combine(testsFolder, "Scribe.LogAppendChild", "bin", rest, "Scribe.LogAppendChild.exe");
        if (File.Exists(mapped))
        {
            return mapped;
        }

        var configuration = rest.Split(separator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var bin = Path.Combine(testsFolder, "Scribe.LogAppendChild", "bin", configuration);
        var found = Directory.Exists(bin)
            ? Directory.GetFiles(bin, "Scribe.LogAppendChild.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        Assert.True(found is not null, "Scribe.LogAppendChild.exe was not built beside the tests: " + mapped);
        return found!;
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts])).ReplaceLineEndings("\n");

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

    // The lines tests\Scribe.LogAppendChild writes (its ChildLine), so a torn or overwritten line never passes for a whole one.
    private static class ChildLine
    {
        internal static string Format(string tag, int i) =>
            $"{tag} {i:D6} " + new string((char)('a' + (i % 26)), 40 + (i * 37 % 120)) + (i % 7 == 0 ? " \u00e9\u4e2d\U0001F600" : string.Empty);
    }
}
