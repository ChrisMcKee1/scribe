using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
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

    private static StartupRegistration Create(FakeBackend backend) =>
        new(backend, NullLogger<StartupRegistration>.Instance);

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
