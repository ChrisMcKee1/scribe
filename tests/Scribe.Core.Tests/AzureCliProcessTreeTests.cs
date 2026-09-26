using System.Diagnostics;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class AzureCliProcessTreeTests
{
    // A hang guard, never the verdict: every wait below is for something certain to happen.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly DateTime T0 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Local);

    private static ProcessNode Node(int id, int parent, string image, int secondsAfterT0) =>
        new(id, parent, image, T0.AddSeconds(secondsAfterT0));

    // az login's browser flow: az.cmd's cmd.exe runs az's python.exe, and MSAL opens the sign-in page with os.startfile,
    // so a browser that was not already running is python.exe's child. conhost.exe hosts the console.
    private static readonly ProcessNode[] AzLoginWithABrowser =
    [
        Node(4, 0, "explorer.exe", -3600),
        Node(100, 4, "cmd.exe", 0),
        Node(101, 100, "python.exe", 1),
        Node(104, 100, "conhost.exe", 1),
        Node(102, 101, "chrome.exe", 2),
        Node(103, 102, "chrome.exe", 3),
        Node(105, 102, "chrome.exe", 3),
    ];

    [Fact]
    public void Ending_az_leaves_a_browser_it_opened_running_with_everything_under_it()
    {
        var selected = AzureCliProcessTree.Select(100, AzLoginWithABrowser).Select(node => node.Id);

        Assert.Equal([100, 101], selected);
    }

    // What Process.Kill(entireProcessTree: true) ended until now: every descendant that started after its parent
    // (KillTree and IsParentOf in Process.Win32.cs), the browser and all its tabs included.
    [Fact]
    public void The_whole_tree_kill_it_replaces_took_the_browser_too()
    {
        var entireTree = EntireTree(100, AzLoginWithABrowser);

        Assert.Equal([100, 101, 104, 102, 103, 105], entireTree);
        Assert.Contains(102, entireTree);
        Assert.DoesNotContain(102, AzureCliProcessTree.Select(100, AzLoginWithABrowser).Select(node => node.Id));
    }

    [Fact]
    public void Az_s_own_processes_are_followed_at_any_depth_and_parents_come_first()
    {
        ProcessNode[] snapshot =
        [
            Node(200, 4, "az.exe", 0),
            Node(201, 200, "python.exe", 1),
            Node(202, 201, "python.exe", 2),
            Node(203, 202, "cmd.exe", 3),
            Node(204, 203, "msedge.exe", 4),
        ];

        Assert.Equal([200, 201, 202, 203], AzureCliProcessTree.Select(200, snapshot).Select(node => node.Id));
    }

    [Fact]
    public void Image_names_match_without_regard_to_case_or_extension()
    {
        ProcessNode[] snapshot =
        [
            Node(300, 4, "CMD.EXE", 0),
            Node(301, 300, "Python.exe", 1),
            Node(302, 300, "az", 1),
            Node(303, 300, "pythonw.exe", 1),
            Node(304, 300, "notpython.exe", 1),
            Node(305, 300, "py.exe", 1),
        ];

        Assert.Equal([300, 301, 302, 303], AzureCliProcessTree.Select(300, snapshot).Select(node => node.Id));
    }

    // A pip install of azure-cli in the Microsoft Store's Python runs "python -m azure.cli", and the Store alias runs
    // the versioned python3.X.exe, which an exact "python" missed: cmd.exe was ended and az login ran on, outside the
    // gate. Only processes reached through az's own chain are candidates, so a user's own Python is never at risk.
    [Fact]
    public void A_store_python_running_az_is_az_s_own()
    {
        ProcessNode[] snapshot =
        [
            Node(700, 4, "cmd.exe", 0),
            Node(701, 700, "python3.12.exe", 1),
            Node(702, 701, "msedge.exe", 2),
            Node(703, 4, "python3.12.exe", 1),
        ];

        Assert.Equal([700, 701], AzureCliProcessTree.Select(700, snapshot).Select(node => node.Id));
    }

    // The root is ended, and gone, before the snapshot, so nothing it starts can be missed.
    [Fact]
    public void The_root_is_ended_and_gone_before_the_snapshot_is_taken()
    {
        var world = new ProcessWorld();
        var root = world.Start(800, 4, "cmd.exe");
        world.Start(801, 800, "python.exe");

        AzureCliProcessTree.End(root, world);

        Assert.Equal(["kill 800", "wait for the root", "snapshot", "kill 801"], world.Calls);
    }

    // The race the order closes: cmd.exe starts az's python just as it is being ended. Taking the snapshot first
    // missed that python, which then ran on outside the gate; for az login it would open a sign-in window for an
    // attempt already retired.
    [Fact]
    public void A_python_the_root_starts_just_before_it_is_ended_is_ended_too()
    {
        var world = new ProcessWorld();
        var root = world.Start(900, 4, "cmd.exe");
        world.StartOnKill(900, new ProcessNode(901, 900, "python.exe", null));

        AzureCliProcessTree.End(root, world);

        Assert.False(world.IsAlive(900));
        Assert.False(world.IsAlive(901));
    }

    // Parent ids outlive their processes and are reused, so an older python.exe can name az's cmd.exe as its parent.
    [Fact]
    public void An_older_process_whose_parent_id_was_reused_is_never_taken_for_az_s()
    {
        ProcessNode[] snapshot =
        [
            Node(400, 4, "cmd.exe", 0),
            Node(401, 400, "python.exe", -60),
            Node(402, 400, "python.exe", 1),
        ];

        Assert.Equal([400, 402], AzureCliProcessTree.Select(400, snapshot).Select(node => node.Id));
    }

    [Fact]
    public void A_process_whose_start_time_cannot_be_read_is_not_followed()
    {
        ProcessNode[] snapshot =
        [
            Node(500, 4, "cmd.exe", 0),
            new(501, 500, "python.exe", null),
            Node(502, 501, "python.exe", 2),
        ];

        Assert.Equal([500], AzureCliProcessTree.Select(500, snapshot).Select(node => node.Id));
    }

    [Fact]
    public void The_root_is_always_ended_even_when_the_snapshot_missed_it()
    {
        Assert.Equal([600], AzureCliProcessTree.Select(600, []).Select(node => node.Id));
    }

    // The same rule end to end, on real processes. A stands in for az.cmd: it starts a ping that stands in for the
    // browser, and a cmd.exe that stands in for az's own child and runs a second ping.
    [Fact]
    public void Ending_a_real_az_tree_spares_what_is_not_az_s_where_the_whole_tree_kill_did_not()
    {
        using (var tree = RealTree.Start())
        {
            AzureCliProcessTree.End(tree.Root);

            Assert.True(tree.Root.WaitForExit(Bound), "the root was not ended");
            Assert.True(WaitUntilExited(tree.AzChild), "az's own child was not ended");
            Assert.False(RealTree.HasExited(tree.Browser), "a process that is not az's was ended");
            Assert.False(RealTree.HasExited(tree.BrowserUnderAzChild), "a process under az's child that is not az's was ended");
        }

        using (var tree = RealTree.Start())
        {
            tree.Root.Kill(entireProcessTree: true);

            Assert.True(tree.Root.WaitForExit(Bound));
            Assert.True(WaitUntilExited(tree.Browser), "the whole tree kill was expected to end the stand-in browser");
            Assert.True(WaitUntilExited(tree.BrowserUnderAzChild));
        }
    }

    /// <summary>
    /// Processes as End sees them. A process can be set to start a child at the moment it is ended, which is how the
    /// race between az.cmd starting python and being killed is reproduced. As the real snapshot does, it always
    /// includes the root, whose id and start time outlive it while Scribe holds its handle.
    /// </summary>
    private sealed class ProcessWorld : AzureCliProcessTree.IProcessOperations
    {
        private readonly Dictionary<int, ProcessNode> _alive = [];
        private readonly Dictionary<int, ProcessNode> _startsOnKill = [];
        private ProcessNode? _root;
        private int _clock;

        public List<string> Calls { get; } = [];

        public ProcessNode Start(int id, int parent, string image)
        {
            var node = new ProcessNode(id, parent, image, T0.AddSeconds(++_clock));
            _alive[id] = node;
            _root ??= node;
            return node;
        }

        public void StartOnKill(int id, ProcessNode child) => _startsOnKill[id] = child;

        public bool IsAlive(int id) => _alive.ContainsKey(id);

        public void Kill(ProcessNode process)
        {
            Calls.Add($"kill {process.Id}");
            if (_startsOnKill.Remove(process.Id, out var child))
            {
                Start(child.Id, child.ParentId, child.ImageName);
            }

            if (_alive.TryGetValue(process.Id, out var live) && live.StartTime == process.StartTime)
            {
                _alive.Remove(process.Id);
            }
        }

        public void WaitForRootExit(TimeSpan limit) => Calls.Add("wait for the root");

        public IReadOnlyCollection<ProcessNode> Snapshot()
        {
            Calls.Add("snapshot");
            var nodes = _alive.Values.ToList();
            if (_root is { } root && !_alive.ContainsKey(root.Id))
            {
                nodes.Add(root);
            }

            return nodes;
        }
    }

    private static bool WaitUntilExited(ProcessNode node)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!RealTree.HasExited(node))
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    private static List<int> EntireTree(int rootId, IReadOnlyCollection<ProcessNode> snapshot)
    {
        var byId = snapshot.ToDictionary(node => node.Id);
        var result = new List<int> { rootId };
        var pending = new Queue<int>([rootId]);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in snapshot.Where(node => node.ParentId == parent
                         && byId[parent].StartTime < node.StartTime))
            {
                result.Add(child.Id);
                pending.Enqueue(child.Id);
            }
        }

        return result;
    }

    /// <summary>A real process tree shaped like az login with a browser open, which ends itself within a minute.</summary>
    private sealed class RealTree : IDisposable
    {
        private readonly List<ProcessNode> _all;

        private RealTree(Process root, ProcessNode azChild, ProcessNode browser, ProcessNode browserUnderAzChild)
        {
            Root = root;
            AzChild = azChild;
            Browser = browser;
            BrowserUnderAzChild = browserUnderAzChild;
            _all = [azChild, browser, browserUnderAzChild];
        }

        public Process Root { get; }

        public ProcessNode AzChild { get; }

        public ProcessNode Browser { get; }

        public ProcessNode BrowserUnderAzChild { get; }

        public static RealTree Start()
        {
            var root = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c \"start \"\" /b ping -n 60 127.0.0.1 >nul & cmd.exe /d /c ping -n 60 127.0.0.1 >nul\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            var deadline = DateTime.UtcNow + Bound;
            while (true)
            {
                var snapshot = AzureCliProcessTree.Snapshot(root);
                var browser = snapshot.FirstOrDefault(node => node.ParentId == root.Id && Image(node) == "ping");
                var azChild = snapshot.FirstOrDefault(node => node.ParentId == root.Id && Image(node) == "cmd");
                var underAzChild = azChild.Id == 0
                    ? default
                    : snapshot.FirstOrDefault(node => node.ParentId == azChild.Id && Image(node) == "ping");
                if (browser.Id != 0 && underAzChild.Id != 0)
                {
                    return new RealTree(root, azChild, browser, underAzChild);
                }

                if (DateTime.UtcNow > deadline)
                {
                    root.Kill(entireProcessTree: true);
                    root.Dispose();
                    throw new TimeoutException("The stand-in az tree did not start.");
                }

                Thread.Sleep(50);
            }
        }

        public static bool HasExited(ProcessNode node)
        {
            try
            {
                using var process = Process.GetProcessById(node.Id);
                return process.HasExited || process.StartTime != node.StartTime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            foreach (var node in _all.Where(node => !HasExited(node)))
            {
                try
                {
                    using var process = Process.GetProcessById(node.Id);
                    process.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            try
            {
                if (!Root.HasExited)
                {
                    Root.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }

            Root.Dispose();
        }

        private static string Image(ProcessNode node) =>
            Path.GetFileNameWithoutExtension(node.ImageName).ToLowerInvariant();
    }
}
