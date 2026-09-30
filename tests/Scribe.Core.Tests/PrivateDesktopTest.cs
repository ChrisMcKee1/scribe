using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using System.Windows.Threading;

namespace Scribe.Core.Tests;

internal static class PrivateDesktopTest
{
    private const string Prefix = "ScribeGridTests-";
    private static readonly TimeSpan RenderCheckpointTimeout = TimeSpan.FromSeconds(10);
    private static string _phase = "not started";

    public static string Phase => Volatile.Read(ref _phase);

    public static void Step(string phase) => Volatile.Write(ref _phase, phase);

    public static void RenderCheckpoint(string stage, Action? updateLayout = null)
    {
        Step($"{stage}: queued");
        try
        {
            // A FIFO render marker waits for earlier rendering, not for an animated window to become idle.
            Dispatcher.CurrentDispatcher.Invoke(() =>
            {
                Step($"{stage}: render");
                updateLayout?.Invoke();
            }, DispatcherPriority.Render, CancellationToken.None, RenderCheckpointTimeout);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"The private-desktop WPF render checkpoint timed out. phase={Phase}", error);
        }

        Step($"{stage}: complete");
    }

    public static bool IsCurrent
    {
        get
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return name.ToString().StartsWith(Prefix, StringComparison.Ordinal);
        }
    }

    public static void Run(Type testClass, Action action, [CallerMemberName] string testName = "")
    {
        if (!IsCurrent)
        {
            RunChild($"{testClass.FullName}.{testName}");
            return;
        }

        ExceptionDispatchInfo? failure = null;
        Step($"{testName}: starting STA");
        var thread = new Thread(() =>
        {
            try
            {
                Step($"{testName}: body");
                action();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                Step($"dispatcher shutdown after {Phase}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                Step($"{testName}: finished");
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"The private-desktop WPF check did not finish. phase={Phase}; limit=30s");
        failure?.Throw();
    }

    private static void RunChild(string testName)
    {
        var name = Prefix + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var folder = Path.Combine(Path.GetTempPath(), name);
        var process = new ProcessInformation();
        try
        {
            Directory.CreateDirectory(folder);
            var reportPath = Path.Combine(folder, "result.trx");
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            Assert.True(File.Exists(host), "The .NET host for the private-desktop test was not found.");
            var assembly = typeof(PrivateDesktopTest).Assembly.Location;
            var command = new StringBuilder(
                $"\"{host}\" vstest \"{assembly}\" \"--TestCaseFilter:FullyQualifiedName={testName}\" \"--logger:trx;LogFileName={reportPath}\"");
            var startup = new StartupInformation { Size = Marshal.SizeOf<StartupInformation>(), Desktop = name };
            // STA startup creates an OLE window before a managed thread runs. Isolate the process from birth instead.
            if (!CreateProcess(host, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero,
                AppContext.BaseDirectory, ref startup, out process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var wait = WaitForSingleObject(process.Process, 90000);
            if (wait != 0)
            {
                using var child = Process.GetProcessById((int)process.ProcessId);
                child.Kill(entireProcessTree: true);
                child.WaitForExit(10000);
                Assert.Fail($"The private-desktop test did not finish: wait result {wait}.");
            }

            Assert.True(GetExitCodeProcess(process.Process, out var exitCode));
            Assert.True(File.Exists(reportPath), $"The private-desktop test produced no result, exit code {exitCode}.");
            var report = XDocument.Load(reportPath);
            var errors = string.Join(Environment.NewLine,
                report.Descendants().Where(element => element.Name.LocalName is "Message" or "StackTrace").Select(element => element.Value));
            Assert.True(exitCode == 0, errors);
            var counters = Assert.Single(report.Descendants(), element => element.Name.LocalName == "Counters");
            Assert.Equal("1", counters.Attribute("passed")?.Value);
            Assert.Equal("1", counters.Attribute("total")?.Value);
        }
        finally
        {
            if (process.Thread != IntPtr.Zero)
            {
                CloseHandle(process.Thread);
            }

            if (process.Process != IntPtr.Zero)
            {
                CloseHandle(process.Process);
            }

            Assert.True(CloseDesktop(desktop), "The test's private desktop could not be released.");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devMode, uint flags, uint access, IntPtr security);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInformation startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
