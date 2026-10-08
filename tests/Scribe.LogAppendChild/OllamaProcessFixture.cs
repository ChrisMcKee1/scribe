using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Scribe.Core.Cleanup;

internal static class OllamaProcessFixture
{
    internal static int Run(string role, string mapping, string readyEvent)
    {
        if (role == "leaf")
        {
            Thread.Sleep(TimeSpan.FromSeconds(45));
            return 0;
        }

        if (role == "server")
        {
            using var child = Process.Start(Info("leaf", mapping, readyEvent))
                ?? throw new InvalidOperationException("The dummy descendant did not start.");
            using var shared = MemoryMappedFile.OpenExisting(mapping);
            using var view = shared.CreateViewAccessor();
            view.Write(0, Environment.ProcessId);
            view.Write(8, Environment.GetEnvironmentVariable("OLLAMA_HOST") == "127.0.0.1:11434");
            view.Write(12, Environment.GetEnvironmentVariable("OLLAMA_NO_CLOUD") == "1");
            view.Write(24, RetainForProcessLifetime(child.SafeHandle).ToInt64());
            using var ready = EventWaitHandle.OpenExisting(readyEvent);
            ready.Set();
            Thread.Sleep(TimeSpan.FromSeconds(45));
            return 0;
        }

        using var exit = EventWaitHandle.OpenExisting(readyEvent + ".Exit");
        // The production guard is internal; the fixture uses it without making a test-only public API.
        var type = typeof(OllamaServiceController).Assembly.GetType("Scribe.Core.Cleanup.OllamaOwnedProcess", throwOnError: true)!;
        using var owned = (IDisposable)type.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Info("server", mapping, readyEvent)])!;
        using var ownerShared = MemoryMappedFile.OpenExisting(mapping);
        using var ownerView = ownerShared.CreateViewAccessor();
        using var serverReady = EventWaitHandle.OpenExisting(readyEvent);
        if (!serverReady.WaitOne(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The dummy server did not publish its descendant.");
        }

        // The guard still owns the creation handle here, even if the server has already exited.
        using var server = Process.GetProcessById(ownerView.ReadInt32(0));
        ownerView.Write(16, RetainForProcessLifetime(server.SafeHandle).ToInt64());
        Console.WriteLine("ready");
        Console.Out.Flush();
        var requested = exit.WaitOne(TimeSpan.FromSeconds(45));
        Environment.Exit(requested ? 0 : 3); // The updater's immediate exit skips managed disposal, as does a force-kill.
        return 0;
    }

    private static IntPtr RetainForProcessLifetime(SafeProcessHandle process)
    {
        // No managed owner closes this copy, even while an exception unwinds. The observer duplicates it from
        // this creator's exact process handle. Exit closes it, and then transfer fails instead of reopening a PID.
        // Every role has a bounded lifetime, so an absent observer cannot leave the copy or its helper behind.
        if (!DuplicateHandle(new IntPtr(-1), process, new IntPtr(-1), out var retained, 0, false, 2))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return retained;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess, out IntPtr target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    internal static ProcessStartInfo Info(string role, string mapping, string readyEvent)
    {
        var info = (ProcessStartInfo)typeof(OllamaServiceController).GetMethod("StartInfo", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Environment.ProcessPath!])!;
        info.ArgumentList.Clear();
        foreach (var argument in new[] { "ollama-fixture", role, mapping, readyEvent })
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}
