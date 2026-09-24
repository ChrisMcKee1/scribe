using System.Diagnostics;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class AzureCliProcessTreeTests
{
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
            Node(303, 300, "pythonista.exe", 1),
        ];

        Assert.Equal([300, 301, 302], AzureCliProcessTree.Select(300, snapshot).Select(node => node.Id));
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

            Assert.True(tree.Root.WaitForExit(TimeSpan.FromSeconds(5)), "the root was not ended");
            Assert.True(WaitUntilExited(tree.AzChild), "az's own child was not ended");
            Assert.False(RealTree.HasExited(tree.Browser), "a process that is not az's was ended");
            Assert.False(RealTree.HasExited(tree.BrowserUnderAzChild), "a process under az's child that is not az's was ended");
        }

        using (var tree = RealTree.Start())
        {
            tree.Root.Kill(entireProcessTree: true);

            Assert.True(tree.Root.WaitForExit(TimeSpan.FromSeconds(5)));
            Assert.True(WaitUntilExited(tree.Browser), "the whole tree kill was expected to end the stand-in browser");
            Assert.True(WaitUntilExited(tree.BrowserUnderAzChild));
        }
    }

    private static bool WaitUntilExited(ProcessNode node)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
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

            var deadline = DateTime.UtcNow.AddSeconds(15);
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
