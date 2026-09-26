using System.Collections.Frozen;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Remote Desktop and virtual machine clients: processes whose window forwards the local keyboard into another machine.
/// Two things change for such a target. Its client can register a low-level keyboard hook of its own, which runs before
/// Scribe's whenever it registers after it (see <c>HotkeyService</c>), and it sends every injected keystroke on to a
/// remote input stack, which a burst of hundreds of events reaches all at once (see <see cref="TypingPace"/>).
/// <para>
/// Judged by process name (no extension, compared case-insensitively), the way
/// <see cref="InjectionTextFormatter.IsTerminalProcess"/> judges terminals. Only clients with evidence that their window
/// forwards the local keyboard are listed; the RD report gives it for each name. The Windows App's own shell
/// (Windows365) is not listed: its sessions open in msrdc, which is. Nor is Citrix's 64-bit HDX engine, whose process
/// name Citrix's documentation does not give.
/// </para>
/// </summary>
public static class RemoteClientProcesses
{
    private static readonly FrozenSet<string> Names = new[]
    {
        // Microsoft: Remote Desktop Connection; the Remote Desktop client for Windows and the Windows App, whose session
        // windows belong to msrdc; Sysinternals Remote Desktop Connection Manager and Hyper-V's Virtual Machine Connection,
        // which both host the Remote Desktop control (mstscax.dll, which registers Windows hooks).
        "mstsc",
        "msrdc",
        "RDCMan",
        "vmconnect",

        // VMware Workstation and Workstation Player; VMware (Omnissa) Horizon Client, whose remote desktop is drawn by its
        // remote MKS (mouse, keyboard and screen) process.
        "vmware",
        "vmplayer",
        "vmware-view",
        "vmware-remotemks",

        // Oracle VirtualBox: the process that runs a VM and shows its window.
        "VirtualBoxVM",

        // Citrix Workspace: the HDX engine and the Desktop Viewer.
        "wfica32",
        "CDViewer",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every process name listed, for tests and documentation.</summary>
    public static IReadOnlyCollection<string> KnownProcessNames => Names;

    /// <summary>
    /// True when <paramref name="processName"/> (as <c>Process.ProcessName</c> gives it, or with its ".exe") is a Remote
    /// Desktop or virtual machine client.
    /// </summary>
    public static bool IsRemoteClient(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return Names.Contains(name);
    }
}
