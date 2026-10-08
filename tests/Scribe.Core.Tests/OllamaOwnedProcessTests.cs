using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class OllamaOwnedProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_immediate_or_abrupt_owner_exit_stops_its_server_and_descendants_but_not_an_outside_instance(bool abrupt)
    {
        var id = Guid.NewGuid().ToString("N");
        var mappingName = "Local\\Scribe.OllamaFixture.Map." + id;
        var readyName = "Local\\Scribe.OllamaFixture.Ready." + id;
        using var shared = MemoryMappedFile.CreateNew(mappingName, 32);
        using var view = shared.CreateViewAccessor();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, readyName + ".Exit");
        var info = Info("owner", mappingName, readyName);
        info.RedirectStandardOutput = true;
        using var owner = Process.Start(info) ?? throw new InvalidOperationException("The dummy owner did not start.");
        Process? outside = null;
        Process? server = null;
        Process? descendant = null;
        try
        {
            outside = Process.Start(Info("leaf", mappingName, readyName))
                ?? throw new InvalidOperationException("The outside dummy did not start.");
            Assert.Equal("ready", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10)), "The dummy server did not publish its descendant.");
            server = ReceiveFixtureProcess(owner.SafeHandle, view.ReadInt64(16));
            descendant = ReceiveFixtureProcess(server.SafeHandle, view.ReadInt64(24));
            Assert.True(view.ReadBoolean(8), "The child did not receive the loopback bind setting.");
            Assert.True(view.ReadBoolean(12), "The child did not receive the cloud-off setting.");
            Assert.False(server.HasExited);
            Assert.False(descendant.HasExited);
            if (abrupt)
            {
                owner.Kill();
            }
            else
            {
                exit.Set();
            }

            Assert.True(owner.WaitForExit(TimeSpan.FromSeconds(10)));
            if (!abrupt)
            {
                Assert.Equal(0, owner.ExitCode);
            }

            Assert.False(outside.HasExited);
            Assert.True(server.WaitForExit(TimeSpan.FromSeconds(10)), "The owned dummy server outlived its owner.");
            Assert.True(descendant.WaitForExit(TimeSpan.FromSeconds(10)), "The owned descendant outlived its owner.");
        }
        finally
        {
            End(owner, server, descendant, outside);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_or_disposal_after_the_server_exits_also_retires_its_descendants(bool selfExited)
    {
        var id = Guid.NewGuid().ToString("N");
        var mappingName = "Local\\Scribe.OllamaFixture.Map." + id;
        var readyName = "Local\\Scribe.OllamaFixture.Ready." + id;
        using var shared = MemoryMappedFile.CreateNew(mappingName, 32);
        using var view = shared.CreateViewAccessor();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var owned = OllamaOwnedProcess.Start(Info("server", mappingName, readyName));
        Process? outside = null;
        Process? server = null;
        Process? descendant = null;
        try
        {
            outside = Process.Start(Info("leaf", mappingName, readyName))
                ?? throw new InvalidOperationException("The outside dummy did not start.");
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10)), "The dummy server did not publish its descendant.");
            // owned retains the server's creation handle throughout this lookup, including an early server exit.
            server = HoldFixtureProcess(view.ReadInt32(0));
            descendant = ReceiveFixtureProcess(server.SafeHandle, view.ReadInt64(24));
            Assert.False(server.HasExited);
            Assert.False(descendant.HasExited);
            if (selfExited)
            {
                server.Kill();
                Assert.True(server.WaitForExit(TimeSpan.FromSeconds(10)));
                Assert.True(owned.HasExited);
                Assert.False(descendant.HasExited);
            }
            else
            {
                owned.Kill();
                Assert.True(owned.WaitForExit(3_000));
            }

            owned.Dispose();

            Assert.True(descendant.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.False(outside.HasExited);
        }
        finally
        {
            try
            {
                owned.Dispose();
            }
            finally
            {
                End(server, descendant, outside);
            }
        }
    }

    [Fact]
    public void A_failed_guarded_creation_reports_a_Windows_error_without_an_unguarded_fallback()
    {
        var missing = Path.Combine(Environment.CurrentDirectory, "missing-ollama-fixture-" + Guid.NewGuid().ToString("N") + ".exe");
        var failure = Assert.Throws<Win32Exception>(() => OllamaOwnedProcess.Start(OllamaServiceController.StartInfo(missing)));

        Assert.Equal(2, failure.NativeErrorCode);
    }

    private static ProcessStartInfo Info(string role, string mapping, string readyEvent)
    {
        var info = OllamaServiceController.StartInfo(AppendOnlyLogTests.ChildExecutable());
        info.ArgumentList.Clear();
        foreach (var argument in new[] { "ollama-fixture", role, mapping, readyEvent })
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static Process HoldFixtureProcess(int processId)
    {
        // The caller already holds this identity, through a transferred handle or its own creation guard.
        var process = Process.GetProcessById(processId);
        try
        {
            _ = process.SafeHandle;
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static Process ReceiveFixtureProcess(SafeProcessHandle creator, long publishedHandle)
    {
        return WithTransferredIdentity(
            () => DuplicateFixtureHandle(creator, publishedHandle),
            identity =>
            {
                var processId = GetProcessId(identity);
                if (processId == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return HoldFixtureProcess(checked((int)processId));
            });
    }

    internal static TProcess WithTransferredIdentity<TIdentity, TProcess>(
        Func<TIdentity> duplicate, Func<TIdentity, TProcess> bind) where TIdentity : IDisposable
    {
        using var identity = duplicate();
        return bind(identity);
    }

    private static SafeProcessHandle DuplicateFixtureHandle(SafeProcessHandle creator, long publishedHandle)
    {
        if (!DuplicateHandle(creator, new IntPtr(publishedHandle), new IntPtr(-1), out var identity, 0, false, 2))
        {
            var error = Marshal.GetLastPInvokeError();
            identity.Dispose();
            throw new Win32Exception(error);
        }

        return identity;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        SafeProcessHandle sourceProcess, IntPtr source, IntPtr targetProcess, out SafeProcessHandle target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    private static void End(params Process?[] processes)
    {
        List<Exception>? failures = null;
        foreach (var process in processes)
        {
            if (process is null)
            {
                continue;
            }

            try
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }

                    Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
                }
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Fixture cleanup did not finish successfully.", failures);
        }
    }
}
