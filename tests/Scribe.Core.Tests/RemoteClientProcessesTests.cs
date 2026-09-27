using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// Which targets are Remote Desktop or virtual machine clients: processes whose window forwards the local keyboard into
/// another machine. Judged by process name, as <see cref="InjectionTextFormatter.IsTerminalProcess"/> judges terminals.
/// The expected set is written out here by hand, from the evidence the RD report cites for each name, so a name dropped
/// from or added to the production list fails this test instead of agreeing with itself.
/// </summary>
public class RemoteClientProcessesTests
{
    private static readonly string[] Expected =
    [
        "mstsc", // Remote Desktop Connection
        "msrdc", // the Remote Desktop client for Windows and the Windows App: the session window (the user's log: target=msrdc)
        "RDCMan", // Sysinternals Remote Desktop Connection Manager, which hosts the Remote Desktop control
        "vmconnect", // Hyper-V Virtual Machine Connection, which hosts the Remote Desktop control
        "vmware", // VMware Workstation
        "vmplayer", // VMware Workstation Player
        "vmware-view", // VMware (Omnissa) Horizon Client
        "vmware-remotemks", // Horizon's remote desktop session window
        "VirtualBoxVM", // the VirtualBox process that runs a VM and its window
        "wfica32", // Citrix Workspace's HDX engine
        "CDViewer", // Citrix Workspace's Desktop Viewer
    ];

    public static TheoryData<string> Names => [.. Expected];

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_listed_client_is_a_remote_client_whatever_the_case_spacing_or_extension(string name)
    {
        Assert.True(RemoteClientProcesses.IsRemoteClient(name));
        Assert.True(RemoteClientProcesses.IsRemoteClient(name.ToUpperInvariant()));
        Assert.True(RemoteClientProcesses.IsRemoteClient(name.ToLowerInvariant()));
        Assert.True(RemoteClientProcesses.IsRemoteClient($"  {name} "));
        Assert.True(RemoteClientProcesses.IsRemoteClient(name + ".exe"));
        Assert.True(RemoteClientProcesses.IsRemoteClient(name + ".EXE"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    [InlineData("notepad")]
    [InlineData("WINWORD")]
    [InlineData("msedge")]
    [InlineData("chrome")]
    [InlineData("explorer")]
    [InlineData("Code")]
    [InlineData("WindowsTerminal")]
    [InlineData("Scribe")]
    [InlineData("Windows365")] // the Windows App's own shell: its sessions open in msrdc
    [InlineData("msrdcw")] // the Remote Desktop client's workspace list, not a session
    [InlineData("VirtualBox")] // the VirtualBox manager, not a VM
    [InlineData("vmware-vmx")] // the VMware VM process, which owns no window of the session
    [InlineData("mstsc2")]
    [InlineData("xmstsc")]
    [InlineData("mstsc.exe.exe")]
    public void Other_programs_are_not_remote_clients(string? name) =>
        Assert.False(RemoteClientProcesses.IsRemoteClient(name));

    [Fact]
    public void The_list_is_exactly_the_clients_the_evidence_names() =>
        Assert.Equal(
            Expected.Order(StringComparer.OrdinalIgnoreCase),
            RemoteClientProcesses.KnownProcessNames.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
}
