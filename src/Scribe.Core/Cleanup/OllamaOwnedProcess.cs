using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Scribe.Core.Cleanup;

/// <summary>The explicitly started server and its descendants cannot outlive Scribe, even without managed teardown.</summary>
internal sealed class OllamaOwnedProcess(SafeFileHandle job, SafeProcessHandle process) : IOllamaOwnedProcess
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    private const nuint JobListAttribute = 0x2000D;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint WaitTimeout = 258;

    internal static OllamaOwnedProcess Start(ProcessStartInfo info)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        SafeProcessHandle? process = null;
        var attributes = IntPtr.Zero;
        var jobValue = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var initialized = false;
        var transferred = false;
        try
        {
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var limits = new JobLimits { Basic = new BasicLimits { Flags = KillOnJobClose } };
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            attributes = Marshal.AllocHGlobal(checked((nint)size));
            jobValue = Marshal.AllocHGlobal(IntPtr.Size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            initialized = true;
            Marshal.WriteIntPtr(jobValue, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, JobListAttribute, jobValue, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            environment = Marshal.StringToHGlobalUni(string.Join('\0',
                info.Environment.Where(value => value.Value is not null)
                    .OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(value => $"{value.Key}={value.Value}")) + "\0\0");
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>() },
                Attributes = attributes,
            };
            var command = new StringBuilder(string.Join(' ',
                new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));

            // Assign at creation, not after Process.Start: a host exit or an early descendant otherwise has a gap.
            // The job handle is not inherited. If any protection step fails, no unguarded process is started.
            if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment | (info.CreateNoWindow ? CreateNoWindow : 0),
                environment, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory,
                ref startup, out var created))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            using var thread = new SafeWaitHandle(created.Thread, ownsHandle: true);
            process = new SafeProcessHandle(created.Process, ownsHandle: true);
            var owned = new OllamaOwnedProcess(job, process);
            transferred = true;
            return owned;
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(jobValue);
            Marshal.FreeHGlobal(environment);
            if (!transferred)
            {
                job.Dispose();
                process?.Dispose();
            }

            GC.KeepAlive(job);
        }
    }

    public bool HasExited => WaitForExit(0);

    public void Kill()
    {
        if (!TerminateJobObject(job, 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public bool WaitForExit(int milliseconds)
    {
        var result = WaitForSingleObject(process, checked((uint)milliseconds));
        return result switch
        {
            0 => true,
            WaitTimeout => false,
            _ => throw new Win32Exception(Marshal.GetLastPInvokeError()),
        };
    }

    public void Dispose()
    {
        job.Dispose();
        process.Dispose();
    }

    private static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }

        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string executable, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInfo process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    // Win32 fills these fields or requires their reserved, zeroed positions in the native structures.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime;
        public long JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority;
        public uint Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong Reads;
        public ulong Writes;
        public ulong Other;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory;
        public nuint JobMemory;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public uint XChars;
        public uint YChars;
        public uint Fill;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public IntPtr ReservedBytes;
        public IntPtr Input;
        public IntPtr Output;
        public IntPtr Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
#pragma warning restore CS0649
}
