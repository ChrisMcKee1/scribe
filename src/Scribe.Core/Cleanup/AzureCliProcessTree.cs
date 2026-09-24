using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Scribe.Core.Cleanup;

/// <summary>One process in a snapshot of the machine's process tree.</summary>
/// <param name="Id">Process id.</param>
/// <param name="ParentId">The id of the process that created it, which may since have exited and been reused.</param>
/// <param name="ImageName">Its executable's file name, for example <c>python.exe</c>.</param>
/// <param name="StartTime">When it started, or null when that could not be read.</param>
public readonly record struct ProcessNode(int Id, int ParentId, string ImageName, DateTime? StartTime);

/// <summary>
/// Ends an Azure CLI invocation that is being cancelled: az's own processes, never a program az started for the user.
/// </summary>
/// <remarks>
/// <para>
/// az runs as cmd.exe (az.cmd) around az's bundled python.exe. In the browser sign-in flow (WAM off, or az before
/// 2.61) MSAL opens the sign-in page through Python's webbrowser module, which on Windows calls os.startfile, so a
/// browser that was not already running starts as python.exe's child, inside az's tree.
/// <c>Process.Kill(entireProcessTree: true)</c> ends every descendant (KillTree in Process.Win32.cs), so cancelling
/// az login closed that browser with all its tabs, perhaps the one where the user had just looked up their tenant ID.
/// </para>
/// <para>
/// So only processes whose image is az's own are followed: the root, and below it cmd, az and any python, whose image
/// is python.exe for az's own install but, for a pip install in the Microsoft Store's Python, the versioned
/// python3.X.exe the Store alias runs. Only processes reached through az's own chain are candidates, so a user's own
/// Python is never at risk. Anything else, and everything under it, is left running. As in KillTree, a child must
/// have started after its parent, so a process whose parent id was merely reused by az is never taken for az's.
/// </para>
/// <para>
/// The root is ended, and its exit awaited, before the snapshot is taken, as KillTree also ends a parent before it
/// looks for its children. A python the root started just before it was ended is then in the snapshot, and nothing
/// can be started after it. The ended root keeps its id and start time while its <see cref="Process"/> handle is open,
/// and its children still name it as their parent.
/// </para>
/// </remarks>
public static partial class AzureCliProcessTree
{
    private const uint SnapProcess = 0x00000002;
    private const nint InvalidHandle = -1;

    // How long the root is given to exit once ended, before the snapshot is taken anyway.
    private static readonly TimeSpan RootExitLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The processes to end for the az invocation rooted at <paramref name="rootId"/>, the root first and every parent
    /// before its children: the root, and each descendant reached only through az's own images (cmd, az, and any image
    /// whose name starts with python), each of which started after its parent.
    /// </summary>
    public static IReadOnlyList<ProcessNode> Select(int rootId, IReadOnlyCollection<ProcessNode> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var byId = new Dictionary<int, ProcessNode>();
        foreach (var node in snapshot)
        {
            byId.TryAdd(node.Id, node);
        }

        var children = snapshot.Where(node => node.Id != node.ParentId).ToLookup(node => node.ParentId);
        var root = byId.TryGetValue(rootId, out var known) ? known : new ProcessNode(rootId, 0, string.Empty, null);
        var selected = new List<ProcessNode> { root };
        var visited = new HashSet<int> { rootId };
        var pending = new Queue<ProcessNode>([root]);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in children[parent.Id])
            {
                if (IsAzureCliImage(child.ImageName)
                    && parent.StartTime is { } parentStart
                    && child.StartTime is { } childStart
                    && parentStart < childStart
                    && visited.Add(child.Id))
                {
                    selected.Add(child);
                    pending.Enqueue(child);
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// Ends the az invocation <paramref name="root"/> started: the root first, and once it has exited, the descendants
    /// <see cref="Select"/> picks from a snapshot taken then. Never throws; a process that has already exited is skipped.
    /// Only the root's exit is awaited, not the end of its redirected output.
    /// </summary>
    public static void End(Process root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var rootNode = new ProcessNode(root.Id, 0, string.Empty, TryReadStartTime(root));
        End(rootNode, new LiveProcesses(root, rootNode.StartTime));
    }

    // The order of End's steps, over operations a test can replace.
    internal static void End(ProcessNode root, IProcessOperations operations)
    {
        operations.Kill(root);
        operations.WaitForRootExit(RootExitLimit);
        foreach (var node in Select(root.Id, operations.Snapshot()).Where(node => node.Id != root.Id))
        {
            operations.Kill(node);
        }
    }

    /// <summary>What <see cref="End(ProcessNode, IProcessOperations)"/> does to processes. None of it throws.</summary>
    internal interface IProcessOperations
    {
        /// <summary>Ends the process, if it is still the one that started at the node's start time.</summary>
        void Kill(ProcessNode process);

        /// <summary>Waits, for up to <paramref name="limit"/>, for the root to exit.</summary>
        void WaitForRootExit(TimeSpan limit);

        /// <summary>The processes below the root as they are now, with the root itself.</summary>
        IReadOnlyCollection<ProcessNode> Snapshot();
    }

    private sealed class LiveProcesses(Process root, DateTime? rootStartTime) : IProcessOperations
    {
        public void Kill(ProcessNode process)
        {
            if (process.Id == root.Id)
            {
                TryKill(root);
                return;
            }

            try
            {
                using var live = Process.GetProcessById(process.Id);

                // The id could have been reused since the snapshot; only the process that was seen is ended.
                if (live.StartTime == process.StartTime)
                {
                    TryKill(live);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Exited since the snapshot.
            }
        }

        public void WaitForRootExit(TimeSpan limit)
        {
            try
            {
                // With a timeout this waits for the exit alone, not for the end of the redirected output.
                root.WaitForExit(limit);
            }
            catch (SystemException)
            {
                // Not started, or already gone; the snapshot is taken either way.
            }
        }

        public IReadOnlyCollection<ProcessNode> Snapshot()
        {
            try
            {
                return AzureCliProcessTree.Snapshot(root, rootStartTime);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return [];
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited, or exiting.
        }
    }

    private static bool IsAzureCliImage(string imageName)
    {
        var name = Path.GetFileNameWithoutExtension(imageName ?? string.Empty);
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("az", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("python", StringComparison.OrdinalIgnoreCase);
    }

    // Every process's parent id and image name (Toolhelp32), with start times read for the root's subtree only, the
    // only part Select can follow. The subtree is gathered through every image here; Select decides what is az's.
    // The root's start time can be passed in, read before it was ended.
    internal static IReadOnlyList<ProcessNode> Snapshot(Process root, DateTime? rootStartTime = null)
    {
        var entries = ReadProcessEntries();
        var byParent = entries.Where(entry => entry.Id != entry.ParentId).ToLookup(entry => entry.ParentId);
        var rootEntry = entries.FirstOrDefault(entry => entry.Id == root.Id);
        var nodes = new List<ProcessNode>
        {
            new(root.Id, rootEntry.ParentId, rootEntry.ImageName ?? string.Empty, rootStartTime ?? TryReadStartTime(root)),
        };

        var seen = new HashSet<int> { root.Id };
        var pending = new Queue<int>([root.Id]);
        while (pending.TryDequeue(out var parentId))
        {
            foreach (var entry in byParent[parentId])
            {
                if (seen.Add(entry.Id))
                {
                    nodes.Add(entry with { StartTime = TryReadStartTime(entry.Id) });
                    pending.Enqueue(entry.Id);
                }
            }
        }

        return nodes;
    }

    private static DateTime? TryReadStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTime? TryReadStartTime(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static unsafe List<ProcessNode> ReadProcessEntries()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var entries = new List<ProcessNode>();
            ProcessEntry32W entry = default;
            entry.Size = (uint)sizeof(ProcessEntry32W);
            for (var more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
            {
                entries.Add(new ProcessNode((int)entry.ProcessId, (int)entry.ParentProcessId, new string(entry.ExeFile), null));
            }

            return entries;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    // PROCESSENTRY32W (tlhelp32.h).
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32FirstW(nint snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32NextW(nint snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
