using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Windows.ApplicationModel;

namespace Scribe.Core.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void Package_identity_selects_the_package_backend_not_the_run_key()
    {
        Assert.IsType<PackagedStartupBackend>(StartupRegistration.CreateBackend(isPackaged: true));
        Assert.IsType<RunKeyStartupBackend>(StartupRegistration.CreateBackend(isPackaged: false));
    }

    [Theory]
    [InlineData(StartupTaskState.Disabled, false, true)]
    [InlineData(StartupTaskState.Enabled, true, true)]
    [InlineData(StartupTaskState.DisabledByUser, false, false)]
    [InlineData(StartupTaskState.DisabledByPolicy, false, false)]
    [InlineData(StartupTaskState.EnabledByPolicy, true, false)]
    public async Task Status_reflects_Windows_not_the_saved_preference(
        StartupTaskState state, bool enabled, bool canChange)
    {
        var backend = new FakeBackend(state);
        var status = await Create(backend).GetStatusAsync();

        Assert.True(status.IsKnown);
        Assert.Equal(enabled, status.IsEnabled);
        Assert.Equal(canChange, status.CanChange);
        Assert.True(status.Matches(enabled));
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
    }

    [Fact]
    public async Task Existing_opt_in_enables_the_new_package_task()
    {
        var backend = new FakeBackend(StartupTaskState.Disabled);
        var status = await Create(backend).SyncAsync(enabled: true);

        Assert.True(status.Matches(true));
        Assert.Equal(1, backend.EnableCalls);
    }

    [Fact]
    public async Task Fresh_install_remains_opt_in()
    {
        var backend = new FakeBackend(StartupTaskState.Disabled);
        var status = await Create(backend).SyncAsync(enabled: false);

        Assert.True(status.Matches(false));
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
    }

    [Theory]
    [InlineData(StartupTaskState.DisabledByUser)]
    [InlineData(StartupTaskState.DisabledByPolicy)]
    public async Task Startup_sync_never_overrides_a_Windows_disable(StartupTaskState state)
    {
        var backend = new FakeBackend(state);
        var status = await Create(backend).SyncAsync(enabled: true);

        Assert.Equal(state, status.State);
        Assert.False(status.IsEnabled);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
    }

    [Theory]
    [InlineData(StartupTaskState.Enabled)]
    [InlineData(StartupTaskState.EnabledByPolicy)]
    public async Task Startup_sync_preserves_external_enable_despite_stale_preference(StartupTaskState state)
    {
        var backend = new FakeBackend(state);
        var status = await Create(backend).SyncAsync(enabled: false);

        Assert.True(status.IsEnabled);
        Assert.Null(status.Error);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
    }

    [Theory]
    [InlineData(StartupTaskState.DisabledByUser, true, "Windows Settings")]
    [InlineData(StartupTaskState.DisabledByPolicy, true, "administrator")]
    [InlineData(StartupTaskState.EnabledByPolicy, false, "organization")]
    public async Task Explicit_changes_report_user_and_policy_blocks(
        StartupTaskState state, bool requested, string message)
    {
        var backend = new FakeBackend(state);
        var status = await Create(backend).SetEnabledAsync(requested);

        Assert.False(status.Matches(requested));
        Assert.False(status.CanChange);
        Assert.Contains(message, status.Message);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
    }

    [Fact]
    public async Task Explicit_disable_disables_the_package_task()
    {
        var backend = new FakeBackend(StartupTaskState.Enabled);
        var status = await Create(backend).SetEnabledAsync(false);

        Assert.True(status.Matches(false));
        Assert.Equal(1, backend.DisableCalls);
    }

    [Fact]
    public async Task Already_enabled_package_task_does_not_request_enable_again()
    {
        var backend = new FakeBackend(StartupTaskState.Enabled);
        var status = await Create(backend).SetEnabledAsync(true);

        Assert.True(status.Matches(true));
        Assert.Equal(0, backend.EnableCalls);
    }

    [Theory]
    [InlineData(StartupTaskState.Disabled)]
    [InlineData(StartupTaskState.DisabledByUser)]
    [InlineData(StartupTaskState.DisabledByPolicy)]
    public async Task Declined_enable_is_not_reported_as_success(StartupTaskState returnedState)
    {
        var backend = new FakeBackend(StartupTaskState.Disabled) { StateAfterEnable = returnedState };
        var status = await Create(backend).SetEnabledAsync(true);

        Assert.False(status.Matches(true));
        Assert.False(status.IsEnabled);
        Assert.NotNull(status.Error);
        Assert.Equal(returnedState, status.State);
    }

    [Fact]
    public async Task Declined_disable_is_not_reported_as_success()
    {
        var backend = new FakeBackend(StartupTaskState.Enabled) { RefuseDisable = true };
        var status = await Create(backend).SetEnabledAsync(false);

        Assert.False(status.Matches(false));
        Assert.True(status.IsEnabled);
        Assert.NotNull(status.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_manifest_task_or_com_failure_is_unavailable_and_can_be_retried(bool missingTask)
    {
        var backend = new FakeBackend(StartupTaskState.Disabled)
        {
            ReadFailure = missingTask
                ? new ArgumentException("Startup task is not registered.")
                : new COMException("Startup task lookup failed."),
        };
        var registration = Create(backend);
        var status = await registration.SyncAsync(true);

        Assert.False(status.IsKnown);
        Assert.False(status.CanChange);
        Assert.False(status.Matches(false));
        Assert.Contains("diagnostics", status.Message);
        Assert.Equal(0, backend.EnableCalls);

        backend.ReadFailure = null;
        Assert.True((await registration.SyncAsync(true)).Matches(true));
    }

    [Fact]
    public async Task Registration_failure_never_claims_enabled()
    {
        var backend = new FakeBackend(StartupTaskState.Disabled)
        {
            EnableFailure = new UnauthorizedAccessException(),
        };
        var status = await Create(backend).SetEnabledAsync(true);

        Assert.False(status.IsKnown);
        Assert.False(status.Matches(true));
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task Unpackaged_sync_refreshes_an_existing_executable_path()
    {
        var backend = new FakeBackend(StartupTaskState.Enabled) { IsPackaged = false };
        var status = await Create(backend).SyncAsync(true);

        Assert.True(status.Matches(true));
        Assert.Equal(1, backend.EnableCalls);
    }

    [Fact]
    public async Task Unpackaged_sync_removes_a_disabled_preference()
    {
        var backend = new FakeBackend(StartupTaskState.Enabled) { IsPackaged = false };
        var status = await Create(backend).SyncAsync(false);

        Assert.True(status.Matches(false));
        Assert.Equal(1, backend.DisableCalls);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void Store_manifest_declares_the_same_opt_in_startup_task_for_each_architecture(string architecture)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var script = File.ReadAllText(Path.Combine(root.FullName, "build", "pack-msix.ps1"));
        var start = script.IndexOf("<?xml version=", StringComparison.Ordinal);
        var end = script.IndexOf("\"@", start, StringComparison.Ordinal);
        var manifest = XDocument.Parse(script[start..end].Replace("$MsixArchitecture", architecture));
        XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        var task = Assert.Single(manifest.Descendants(desktop + "StartupTask"));

        Assert.Equal(StartupRegistration.TaskId, (string?)task.Attribute("TaskId"));
        Assert.Equal("false", (string?)task.Attribute("Enabled"));
        Assert.Equal("$displayName", (string?)task.Attribute("DisplayName"));
        Assert.Equal("windows.startupTask", (string?)task.Parent?.Attribute("Category"));
        Assert.Equal("Scribe.exe", (string?)task.Parent?.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)task.Parent?.Attribute("EntryPoint"));
        Assert.Contains("desktop", ((string?)manifest.Root?.Attribute("IgnorableNamespaces"))?.Split(' ') ?? []);
        Assert.Equal(architecture, (string?)manifest.Root?
            .Element(manifest.Root.Name.Namespace + "Identity")?.Attribute("ProcessorArchitecture"));
    }

    [Fact]
    public async Task Unexpected_exception_types_leave_the_state_unknown_instead_of_escaping()
    {
        // WinRT maps E_NOTIMPL and friends to exception types a narrow filter did not list; one of
        // those escaping left the Settings switch disabled on "Checking..." for good.
        var backend = new FakeBackend(StartupTaskState.Disabled)
        {
            ReadFailure = new NotImplementedException(),
        };

        var status = await Create(backend).GetStatusAsync();

        Assert.False(status.IsKnown);
        Assert.False(status.CanChange);
        Assert.Contains("diagnostics", status.Message);
    }

    // --- Direct-download builds: the per-user Run key plus Windows' own approval record ---------

    [Fact]
    public async Task Run_key_enable_writes_the_quoted_running_executable()
    {
        var runKey = new FakeRunKey();
        var status = await Create(RunKey(runKey)).SetEnabledAsync(true);

        Assert.True(status.Matches(true));
        Assert.Equal($"\"{ExecutablePath}\"", runKey.Value);
    }

    [Fact]
    public async Task Run_key_disable_deletes_the_value()
    {
        var runKey = new FakeRunKey { Value = $"\"{ExecutablePath}\"" };
        var status = await Create(RunKey(runKey)).SetEnabledAsync(false);

        Assert.True(status.Matches(false));
        Assert.Null(runKey.Value);
        Assert.Equal(1, runKey.Deletes);
    }

    [Fact]
    public async Task Run_entry_Windows_has_turned_off_is_reported_as_the_users_choice()
    {
        var runKey = new FakeRunKey { Value = $"\"{ExecutablePath}\"" };
        var status = await Create(RunKey(runKey, StartupApprovalKind.Disabled)).GetStatusAsync();

        Assert.Equal(StartupTaskState.DisabledByUser, status.State);
        Assert.False(status.IsEnabled);
        Assert.False(status.CanChange);
        Assert.Contains("Windows Settings > Apps > Startup", status.Message);
    }

    [Theory]
    [InlineData((int)StartupApprovalKind.NotRecorded)]
    [InlineData((int)StartupApprovalKind.Enabled)]
    [InlineData((int)StartupApprovalKind.Unrecognized)]
    [InlineData((int)StartupApprovalKind.Unreadable)]
    public async Task Run_entry_is_enabled_unless_Windows_records_an_explicit_off(int approval)
    {
        var runKey = new FakeRunKey { Value = $"\"{ExecutablePath}\"" };
        var status = await Create(RunKey(runKey, (StartupApprovalKind)approval)).GetStatusAsync();

        Assert.Equal(StartupTaskState.Enabled, status.State);
        Assert.True(status.CanChange);
    }

    [Fact]
    public async Task Leftover_off_record_without_a_run_value_still_lets_the_user_turn_startup_on()
    {
        // Windows lists nothing to approve until the Run value exists, so reporting the leftover
        // record here would leave no way to get Scribe onto Windows' Startup page at all.
        var runKey = new FakeRunKey();
        var status = await Create(RunKey(runKey, StartupApprovalKind.Disabled)).GetStatusAsync();

        Assert.Equal(StartupTaskState.Disabled, status.State);
        Assert.True(status.CanChange);
    }

    [Fact]
    public async Task Enable_under_a_Windows_side_off_points_to_Windows_instead_of_claiming_success()
    {
        var runKey = new FakeRunKey();
        var status = await Create(RunKey(runKey, StartupApprovalKind.Disabled)).SetEnabledAsync(true);

        Assert.NotNull(runKey.Value);
        Assert.Equal(StartupTaskState.DisabledByUser, status.State);
        Assert.False(status.Matches(true));
        Assert.Contains("Windows Settings > Apps > Startup", status.Message);
        Assert.DoesNotContain("did not accept", status.Message);
    }

    [Fact]
    public async Task Startup_sync_leaves_a_run_entry_Windows_has_turned_off_alone()
    {
        var runKey = new FakeRunKey { Value = "\"C:\\Old\\Scribe.exe\"" };
        var status = await Create(RunKey(runKey, StartupApprovalKind.Disabled)).SyncAsync(true);

        Assert.Equal(StartupTaskState.DisabledByUser, status.State);
        Assert.Null(status.Error);
        Assert.Equal(0, runKey.Writes);
        Assert.Equal("\"C:\\Old\\Scribe.exe\"", runKey.Value);
    }

    [Fact]
    public async Task Startup_sync_removes_a_turned_off_entry_the_saved_preference_no_longer_wants()
    {
        var runKey = new FakeRunKey { Value = $"\"{ExecutablePath}\"" };
        var status = await Create(RunKey(runKey, StartupApprovalKind.Disabled)).SyncAsync(false);

        Assert.True(status.Matches(false));
        Assert.Null(runKey.Value);
    }

    [Fact]
    public async Task Missing_executable_path_is_unavailable_and_writes_nothing()
    {
        var runKey = new FakeRunKey();
        var backend = new RunKeyStartupBackend(runKey, new FakeApproval(StartupApprovalKind.NotRecorded), () => null);
        var status = await Create(backend).SetEnabledAsync(true);

        Assert.False(status.IsKnown);
        Assert.Null(runKey.Value);
    }

    // Community-reported, undocumented: the status byte is even (0x02, 0x06) when Windows lets the
    // entry start and odd (0x03, 0x07) when the user turned it off, followed by padding and a
    // timestamp of the change.
    [Theory]
    [InlineData(null, (int)StartupApprovalKind.NotRecorded)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, (int)StartupApprovalKind.Enabled)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, (int)StartupApprovalKind.Enabled)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, (int)StartupApprovalKind.Disabled)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, (int)StartupApprovalKind.Disabled)]
    [InlineData(new byte[] { 0x07 }, (int)StartupApprovalKind.Disabled)]
    [InlineData(new byte[] { 0x06 }, (int)StartupApprovalKind.Enabled)]
    [InlineData(new byte[0], (int)StartupApprovalKind.Unrecognized)]
    [InlineData("03", (int)StartupApprovalKind.Unrecognized)]
    [InlineData(3, (int)StartupApprovalKind.Unrecognized)]
    public void Low_bit_of_the_status_byte_is_what_marks_a_Windows_side_disable(object? value, int expected)
    {
        Assert.Equal((StartupApprovalKind)expected, StartupApproval.Parse(value).Kind);
    }

    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x03, true)]
    [InlineData(0x06, true)]
    [InlineData(0x07, true)]
    [InlineData(0x01, false)]
    [InlineData(0x04, false)]
    public void Status_bytes_outside_the_reported_set_are_flagged_for_the_log(int marker, bool reported)
    {
        var approval = StartupApproval.Parse(new byte[] { (byte)marker, 0, 0, 0 });

        Assert.Equal(reported, approval.IsReportedMarker);
        Assert.Equal((marker & 1) != 0 ? StartupApprovalKind.Disabled : StartupApprovalKind.Enabled, approval.Kind);
    }

    [Theory]
    [InlineData(0x02, StartupTaskState.Enabled)]
    [InlineData(0x06, StartupTaskState.Enabled)]
    [InlineData(0x03, StartupTaskState.DisabledByUser)]
    [InlineData(0x07, StartupTaskState.DisabledByUser)]
    public async Task Run_entry_state_follows_the_approval_record_Windows_keeps(int marker, StartupTaskState expected)
    {
        var runKey = new FakeRunKey { Value = $"\"{ExecutablePath}\"" };
        var backend = new RunKeyStartupBackend(
            runKey,
            new ParsingApproval(new byte[] { (byte)marker, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            () => ExecutablePath);

        var status = await Create(backend).GetStatusAsync();

        Assert.Equal(expected, status.State);
    }

    // --- What each request is allowed to change -------------------------------------------------

    [Theory]
    [InlineData(true, false, true, StartupTaskState.Disabled, (int)StartupChange.Enable)]
    [InlineData(true, false, true, StartupTaskState.Enabled, (int)StartupChange.None)]
    [InlineData(true, false, false, StartupTaskState.Enabled, (int)StartupChange.Disable)]
    [InlineData(true, true, true, StartupTaskState.Disabled, (int)StartupChange.Enable)]
    [InlineData(true, true, false, StartupTaskState.Enabled, (int)StartupChange.None)]
    [InlineData(true, false, true, StartupTaskState.DisabledByUser, (int)StartupChange.None)]
    [InlineData(true, false, false, StartupTaskState.EnabledByPolicy, (int)StartupChange.None)]
    [InlineData(false, false, true, StartupTaskState.Disabled, (int)StartupChange.Enable)]
    [InlineData(false, true, true, StartupTaskState.Enabled, (int)StartupChange.Enable)]
    [InlineData(false, true, true, StartupTaskState.DisabledByUser, (int)StartupChange.None)]
    [InlineData(false, true, false, StartupTaskState.Enabled, (int)StartupChange.Disable)]
    [InlineData(false, true, false, StartupTaskState.DisabledByUser, (int)StartupChange.Disable)]
    [InlineData(false, true, false, StartupTaskState.Disabled, (int)StartupChange.None)]
    public void Plan_never_overrides_a_Windows_side_decision(
        bool packaged, bool synchronize, bool requested, StartupTaskState state, int expected)
    {
        Assert.Equal((StartupChange)expected, StartupRegistration.Plan(packaged, synchronize, requested, state));
        Assert.Equal(StartupChange.None, StartupRegistration.Plan(packaged, synchronize, requested: null, state));
    }

    // --- The saved preference -------------------------------------------------------------------

    [Theory]
    [InlineData(true, StartupTaskState.Enabled, false, true)]
    [InlineData(false, StartupTaskState.Disabled, true, false)]
    [InlineData(true, StartupTaskState.Disabled, false, false)]
    [InlineData(false, StartupTaskState.Enabled, true, true)]
    [InlineData(true, StartupTaskState.DisabledByUser, false, true)]
    [InlineData(false, StartupTaskState.DisabledByUser, true, false)]
    [InlineData(true, StartupTaskState.DisabledByPolicy, false, false)]
    [InlineData(false, StartupTaskState.EnabledByPolicy, false, true)]
    public void Preference_after_a_request_follows_Windows_or_the_request_it_blocked(
        bool requested, StartupTaskState state, bool saved, bool expected)
    {
        Assert.Equal(expected, StartupPreference.AfterRequest(requested, new StartupRegistrationStatus(state), saved));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unknown_state_keeps_the_saved_preference(bool saved)
    {
        var unknown = new StartupRegistrationStatus(null, "unavailable");

        Assert.Equal(saved, StartupPreference.AfterRequest(!saved, unknown, saved));
        Assert.Equal(saved, StartupPreference.Observed(unknown, saved));
    }

    [Theory]
    [InlineData(StartupTaskState.Enabled, false, true)]
    [InlineData(StartupTaskState.EnabledByPolicy, false, true)]
    [InlineData(StartupTaskState.Disabled, true, false)]
    [InlineData(StartupTaskState.DisabledByPolicy, true, false)]
    [InlineData(StartupTaskState.DisabledByUser, true, true)]
    [InlineData(StartupTaskState.DisabledByUser, false, false)]
    public void Observed_state_is_adopted_so_an_untouched_switch_cannot_undo_a_Windows_change(
        StartupTaskState state, bool saved, bool expected)
    {
        Assert.Equal(expected, StartupPreference.Observed(new StartupRegistrationStatus(state), saved));
    }

    // --- The Settings switch applies immediately ------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Flipping_the_switch_changes_Windows_and_saves_the_preference_at_once(bool requested)
    {
        var backend = new FakeBackend(requested ? StartupTaskState.Disabled : StartupTaskState.Enabled);
        var saves = new List<bool>();
        var toggle = CreateToggle(backend, value => { saves.Add(value); return Task.CompletedTask; });

        var result = await toggle.ApplyAsync(requested, new StartupRegistrationStatus(backend.State), !requested);

        Assert.Equal(StartupToggleOutcome.Applied, result.Outcome);
        Assert.True(result.Status.Matches(requested));
        Assert.Equal(requested, result.Preference);
        Assert.Equal(new[] { requested }, saves);
        Assert.Equal(requested ? 1 : 0, backend.EnableCalls);
        Assert.Equal(requested ? 0 : 1, backend.DisableCalls);
    }

    public static TheoryData<StartupTaskState?, bool> UnchangeableRequests => new()
    {
        { StartupTaskState.Enabled, true },
        { StartupTaskState.Disabled, false },
        { StartupTaskState.DisabledByUser, true },
        { StartupTaskState.DisabledByPolicy, true },
        { StartupTaskState.EnabledByPolicy, false },
        { null, true },
    };

    [Theory]
    [MemberData(nameof(UnchangeableRequests))]
    public async Task Switch_that_matches_Windows_or_is_locked_touches_nothing(StartupTaskState? shown, bool requested)
    {
        var backend = new FakeBackend(shown ?? StartupTaskState.Disabled);
        var saves = 0;
        var toggle = CreateToggle(backend, _ => { saves++; return Task.CompletedTask; });

        var result = await toggle.ApplyAsync(requested, new StartupRegistrationStatus(shown), savedPreference: false);

        Assert.Equal(StartupToggleOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task Preference_is_saved_before_Windows_is_asked()
    {
        // The next launch reconciles Windows with the saved preference, so saving second would let
        // an exit between the steps reverse the change; saving first lets that launch finish it.
        var backend = new FakeBackend(StartupTaskState.Disabled);
        var callsAtSave = -1;
        var toggle = CreateToggle(backend, _ =>
        {
            callsAtSave = backend.EnableCalls + backend.DisableCalls;
            return Task.CompletedTask;
        });

        var result = await toggle.ApplyAsync(true, new StartupRegistrationStatus(StartupTaskState.Disabled), false);

        Assert.Equal(0, callsAtSave);
        Assert.Equal(1, backend.EnableCalls);
        Assert.Equal(StartupToggleOutcome.Applied, result.Outcome);

        // A launch after an exit between the two steps finishes the change rather than reverse it.
        var interrupted = new FakeBackend(StartupTaskState.Disabled);
        var afterRestart = await Create(interrupted).SyncAsync(enabled: true);
        Assert.True(afterRestart.Matches(true));
    }

    [Fact]
    public async Task Second_change_while_one_is_applying_is_refused_without_touching_Windows()
    {
        var backend = new FakeBackend(StartupTaskState.Disabled);
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toggle = CreateToggle(backend, _ => { saveStarted.SetResult(); return saving.Task; });

        var first = toggle.ApplyAsync(true, new StartupRegistrationStatus(StartupTaskState.Disabled), false);
        await saveStarted.Task;
        Assert.True(toggle.IsApplying);

        var second = await toggle.ApplyAsync(false, new StartupRegistrationStatus(StartupTaskState.Enabled), true);

        Assert.Equal(StartupToggleOutcome.Busy, second.Outcome);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);

        saving.SetResult();
        var applied = await first;
        Assert.Equal(StartupToggleOutcome.Applied, applied.Outcome);
        Assert.Equal(1, backend.EnableCalls);
        Assert.Equal(0, backend.DisableCalls);
        Assert.False(toggle.IsApplying);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_save_leaves_Windows_untouched(bool requested)
    {
        var shown = requested ? StartupTaskState.Disabled : StartupTaskState.Enabled;
        var backend = new FakeBackend(shown);
        var toggle = CreateToggle(backend, _ => Task.FromException(new IOException("database is locked")));

        var result = await toggle.ApplyAsync(requested, new StartupRegistrationStatus(shown), !requested);

        Assert.Equal(StartupToggleOutcome.SaveFailed, result.Outcome);
        Assert.Equal(!requested, result.Preference);
        Assert.Equal(!requested, result.Status.IsEnabled);
        Assert.Equal(shown, backend.State);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
        Assert.Contains("left as it was", result.Status.Message);
    }

    [Fact]
    public async Task Declined_enable_reports_why_and_saves_what_Windows_actually_did()
    {
        var backend = new FakeBackend(StartupTaskState.Disabled) { StateAfterEnable = StartupTaskState.Disabled };
        var saves = new List<bool>();
        var toggle = CreateToggle(backend, value => { saves.Add(value); return Task.CompletedTask; });

        var result = await toggle.ApplyAsync(true, new StartupRegistrationStatus(StartupTaskState.Disabled), false);

        Assert.Equal(StartupToggleOutcome.Declined, result.Outcome);
        Assert.False(result.Preference);
        Assert.False(result.Status.IsEnabled);
        Assert.NotNull(result.Status.Error);
        Assert.Equal(new[] { true, false }, saves);
    }

    [Fact]
    public async Task Declined_change_whose_correction_cannot_be_saved_keeps_the_request_for_a_retry()
    {
        var backend = new FakeBackend(StartupTaskState.Enabled) { RefuseDisable = true };
        var saves = 0;
        var toggle = CreateToggle(backend, _ =>
            ++saves == 1 ? Task.CompletedTask : Task.FromException(new IOException("database is locked")));

        var result = await toggle.ApplyAsync(false, new StartupRegistrationStatus(StartupTaskState.Enabled), true);

        Assert.Equal(StartupToggleOutcome.Declined, result.Outcome);
        Assert.True(result.Status.IsEnabled);
        Assert.False(result.Preference);
        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task Windows_side_off_keeps_the_request_so_turning_it_on_in_Windows_later_sticks()
    {
        var runKey = new FakeRunKey();
        var saves = new List<bool>();
        var registration = Create(RunKey(runKey, StartupApprovalKind.Disabled));
        var toggle = new StartupToggle(
            registration,
            value => { saves.Add(value); return Task.CompletedTask; },
            NullLogger.Instance);

        var result = await toggle.ApplyAsync(true, new StartupRegistrationStatus(StartupTaskState.Disabled), false);

        Assert.Equal(StartupToggleOutcome.Declined, result.Outcome);
        Assert.Equal(StartupTaskState.DisabledByUser, result.Status.State);
        Assert.True(result.Preference);
        Assert.Equal(new[] { true }, saves);
        Assert.NotNull(runKey.Value);

        // With "on" saved, the next launch's reconcile leaves Windows' decision alone instead of
        // deleting the entry.
        var afterRestart = await registration.SyncAsync(result.Preference);
        Assert.Equal(StartupTaskState.DisabledByUser, afterRestart.State);
        Assert.NotNull(runKey.Value);
    }

    [Fact]
    public async Task Unreadable_result_claims_nothing_and_leaves_the_request_for_the_next_launch()
    {
        var backend = new FakeBackend(StartupTaskState.Disabled) { EnableFailure = new UnauthorizedAccessException() };
        var saves = new List<bool>();
        var toggle = CreateToggle(backend, value => { saves.Add(value); return Task.CompletedTask; });

        var result = await toggle.ApplyAsync(true, new StartupRegistrationStatus(StartupTaskState.Disabled), false);

        Assert.Equal(StartupToggleOutcome.Unknown, result.Outcome);
        Assert.False(result.Status.IsKnown);
        Assert.False(result.Status.IsEnabled);
        Assert.True(result.Preference);
        Assert.Equal(new[] { true }, saves);
    }

    // --- Isolated data folder (SCRIBE_DATA_DIR) ---------------------------------------------

    [Theory]
    [InlineData(true, true, StartupTaskState.Disabled)]
    [InlineData(true, false, StartupTaskState.Enabled)]
    [InlineData(false, true, StartupTaskState.Disabled)]
    [InlineData(false, true, StartupTaskState.Enabled)]
    [InlineData(false, false, StartupTaskState.Enabled)]
    public async Task Isolated_instance_never_changes_Windows_at_launch(
        bool packaged, bool savedPreference, StartupTaskState state)
    {
        // A direct-download scratch build would otherwise delete the installed app's Run entry
        // ("off") or point it at itself ("on"); a package build would migrate a scratch preference.
        var backend = new FakeBackend(state) { IsPackaged = packaged };
        var registration = new StartupRegistration(backend, NullLogger<StartupRegistration>.Instance, isolatedDataRoot: true);

        var status = await registration.SyncAsync(savedPreference);

        Assert.True(registration.IsIsolated);
        Assert.Equal(state, status.State);
        Assert.Equal(0, backend.EnableCalls + backend.DisableCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Isolated_instance_still_applies_an_explicit_change_from_the_switch(bool packaged)
    {
        var backend = new FakeBackend(StartupTaskState.Disabled) { IsPackaged = packaged };
        var registration = new StartupRegistration(backend, NullLogger<StartupRegistration>.Instance, isolatedDataRoot: true);

        var status = await registration.SetEnabledAsync(true);

        Assert.True(status.Matches(true));
        Assert.Equal(1, backend.EnableCalls);
    }

    [Fact]
    public void Isolation_comes_from_the_data_folder_in_use()
    {
        var isolatedRoot = Path.Combine(Path.GetTempPath(), "scribe-isolated-" + Guid.NewGuid().ToString("N"));

        Assert.True(new StartupRegistration(
            NullLogger<StartupRegistration>.Instance, new AppPaths(rootOverride: null, dataDirOverride: isolatedRoot)).IsIsolated);
        Assert.False(new StartupRegistration(
            NullLogger<StartupRegistration>.Instance, new AppPaths(rootOverride: null, dataDirOverride: null)).IsIsolated);
        Assert.False(Directory.Exists(isolatedRoot));
    }

    [Fact]
    public void Isolated_note_is_plain_and_dash_free()
    {
        Assert.Contains("isolated data folder", StartupRegistration.IsolatedDataNote);
        Assert.DoesNotContain('\u2013', StartupRegistration.IsolatedDataNote);
        Assert.DoesNotContain('\u2014', StartupRegistration.IsolatedDataNote);
    }

    // --- The preference write ------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preference_write_changes_only_its_field_through_the_atomic_update(bool enabled)
    {
        var stored = AppSettings.CreateDefault();
        stored.LaunchOnLogin = !enabled;
        stored.EnableAiCleanup = true;
        var settings = new UpdateOnlySettingsRepository(stored);

        await StartupPreference.PersistAsync(settings, enabled);

        Assert.Equal(1, settings.Updates);
        Assert.Equal(enabled, settings.Stored.LaunchOnLogin);
        Assert.True(settings.Stored.EnableAiCleanup);
    }

    private const string ExecutablePath = @"C:\Apps\Scribe\current\Scribe.exe";

    private static StartupRegistration Create(IStartupRegistrationBackend backend) =>
        new(backend, NullLogger<StartupRegistration>.Instance);

    private static RunKeyStartupBackend RunKey(
        FakeRunKey runKey,
        StartupApprovalKind approval = StartupApprovalKind.NotRecorded) =>
        new(runKey, new FakeApproval(approval), () => ExecutablePath);

    private static StartupToggle CreateToggle(FakeBackend backend, Func<bool, Task> save) =>
        new(Create(backend), save, NullLogger.Instance);

    private sealed class FakeRunKey : IRunKeyStore
    {
        public string? Value { get; set; }
        public int Writes { get; private set; }
        public int Deletes { get; private set; }

        public string? Read() => Value;

        public void Write(string command)
        {
            Writes++;
            Value = command;
        }

        public void Delete()
        {
            Deletes++;
            Value = null;
        }
    }

    private sealed class FakeApproval(StartupApprovalKind kind) : IStartupApprovalReader
    {
        public StartupApproval Read(string valueName)
        {
            Assert.Equal("Scribe", valueName);
            return new StartupApproval(kind);
        }
    }

    private sealed class ParsingApproval(byte[] value) : IStartupApprovalReader
    {
        public StartupApproval Read(string valueName) => StartupApproval.Parse(value);
    }

    // Proves the preference goes through the atomic update: a whole-document load and save would
    // throw here.
    private sealed class UpdateOnlySettingsRepository(AppSettings stored) : ISettingsRepository
    {
        public AppSettings Stored { get; } = stored;
        public int Updates { get; private set; }
        public bool LastLoadFailed => false;

        public AppSettings Load() => throw new InvalidOperationException("Load must not be used for the preference.");

        public void Save(AppSettings settings) =>
            throw new InvalidOperationException("Save must not be used for the preference.");

        public AppSettings Update(Action<AppSettings> mutate)
        {
            Updates++;
            mutate(Stored);
            return Stored;
        }

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) =>
            throw new InvalidOperationException("A checked Update must not be used for the preference.");

        public void SaveBundle(
            AppSettings settings,
            IReadOnlyList<DictionaryEntry>? dictionaryEntries,
            IReadOnlyList<Snippet>? snippets,
            long aiCleanupIntent = 0) =>
            throw new InvalidOperationException("SaveBundle must not be used for the preference.");

        public string? Get(string key) => throw new InvalidOperationException("Get must not be used for the preference.");

        public void Set(string key, string value) =>
            throw new InvalidOperationException("Set must not be used for the preference.");
    }

    private sealed class FakeBackend(StartupTaskState state) : IStartupRegistrationBackend
    {
        public bool IsPackaged { get; init; } = true;
        public StartupTaskState State { get; private set; } = state;
        public StartupTaskState StateAfterEnable { get; init; } = StartupTaskState.Enabled;
        public bool RefuseDisable { get; init; }
        public Exception? ReadFailure { get; set; }
        public Exception? EnableFailure { get; init; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public Task<StartupTaskState> GetStateAsync() =>
            ReadFailure is { } failure ? Task.FromException<StartupTaskState>(failure) : Task.FromResult(State);

        public Task EnableAsync()
        {
            EnableCalls++;
            if (EnableFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            State = StateAfterEnable;
            return Task.CompletedTask;
        }

        public Task DisableAsync()
        {
            DisableCalls++;
            if (!RefuseDisable)
            {
                State = StartupTaskState.Disabled;
            }

            return Task.CompletedTask;
        }
    }
}
