using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using BenchmarkDotNet.Environments;
using Scribe.Core.Diagnostics;

namespace Scribe.Benchmarks;

/// <summary>
/// What is actually executing a measurement: this process's architecture, the OS architecture,
/// whether Windows is emulating the process, the priority it runs at, and the machine's active
/// power plan. Printed by the host at startup and with results, and by every BenchmarkDotNet child
/// process (see <see cref="ExecutionEnvironmentMarker"/>), because the child is a separate process
/// whose architecture and priority are not the host's.
/// </summary>
internal sealed record ExecutionEnvironment(
    Architecture ProcessArchitecture,
    Architecture OsArchitecture,
    bool IsEmulated,
    string Priority,
    string PowerPlan,
    int ProcessorCount,
    string Runtime)
{
    /// <summary>Prefix of the line each benchmark process writes; see <see cref="ExecutionEnvironmentColumn"/>.</summary>
    public const string MarkerPrefix = "// scribe-execution-environment:";

    private static int s_markerWritten;

    public static ExecutionEnvironment Capture()
    {
        // Emulation is decided by the same rule the app's diagnostics use. Since .NET 7,
        // OSArchitecture reports Arm64 inside an emulated x64 process, so the two values differ
        // exactly when Windows is translating this process.
        var capability = ComputeCapabilityReport.Create(
            RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);

        return new ExecutionEnvironment(
            capability.ProcessArchitecture,
            capability.OsArchitecture,
            capability.IsEmulated,
            ReadPriority(),
            PowerPlanProbe.Describe(),
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription);
    }

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"process={ProcessArchitecture} os={OsArchitecture} {(IsEmulated ? "EMULATED" : "native")} " +
        $"priority={Priority} power={PowerPlan} cpus={ProcessorCount} runtime={Runtime}");

    /// <summary>The compact form shown in the summary table.</summary>
    public string Compact() => string.Create(
        CultureInfo.InvariantCulture,
        $"{ProcessArchitecture}{(IsEmulated ? $" emulated on {OsArchitecture}" : " native")}, {Priority}, " +
        $"{PowerPlanProbe.FriendlyPart(PowerPlan)}");

    /// <summary>
    /// Writes this process's environment once, as a marker line the host can find in the benchmark
    /// process output.
    /// </summary>
    public static void WriteMarkerOnce()
    {
        if (Interlocked.Exchange(ref s_markerWritten, 1) != 0)
        {
            return;
        }

        var environment = Capture();
        Console.WriteLine($"{MarkerPrefix} {environment.Compact()} | {environment.Describe()}");
    }

    private static string ReadPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PriorityClass.ToString();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return "unknown";
        }
    }
}

/// <summary>
/// Makes every benchmark process report its own environment without each benchmark class opting
/// in, so a class added later is covered too. The module initializer runs in any process that
/// loads this assembly; a BenchmarkDotNet child starts from a generated entry assembly, while the
/// host's entry assembly is this one and already prints its environment itself. BenchmarkDotNet
/// raises the child's priority right after starting it (<c>DotNetCliExecutor</c>), before the
/// child runtime reaches managed code that could load this assembly.
/// </summary>
internal static class ExecutionEnvironmentMarker
{
    [ModuleInitializer]
    internal static void WriteFromBenchmarkProcess()
    {
        if (Assembly.GetEntryAssembly() == typeof(ExecutionEnvironmentMarker).Assembly)
        {
            return;
        }

        ExecutionEnvironment.WriteMarkerOnce();
    }
}

/// <summary>
/// Chooses the build that matches the architecture actually running the benchmarks, and the dotnet
/// host BenchmarkDotNet must launch it with.
/// <para>
/// BenchmarkDotNet 0.15.8 builds a generated project with any job MSBuild arguments, then runs the
/// resulting DLL through <c>dotnet</c>. On Windows that is whatever <c>dotnet</c> the PATH resolves
/// (<c>DotNetCliCommandExecutor.GetDefaultDotNetCliPath</c>) unless the toolchain supplies a CLI
/// path. The generated project's <c>PlatformTarget</c> is the job platform, which defaults to the
/// architecture of the process running BenchmarkDotNet (<c>EnvironmentResolver</c> registers
/// <c>RuntimeInformation.GetCurrentPlatform</c>). A hardcoded <c>win-x64</c> RID therefore proved
/// nothing about what executed: an x64 host emulated on an Arm64 PC built and ran x64 through
/// whatever the PATH host was, and an Arm64-native host paired <c>win-x64</c> with an ARM64
/// PlatformTarget, which the SDK rejects (NETSDK1032). Pinning all three (RID, platform and host)
/// to this process's architecture makes the child run exactly where the host runs.
/// </para>
/// </summary>
internal static class BenchmarkTarget
{
    public static bool TryResolve(Architecture processArchitecture, out string runtimeIdentifier, out Platform platform)
    {
        switch (processArchitecture)
        {
            case Architecture.X64:
                runtimeIdentifier = "win-x64";
                platform = Platform.X64;
                return true;
            case Architecture.Arm64:
                runtimeIdentifier = "win-arm64";
                platform = Platform.Arm64;
                return true;
            default:
                // Scribe.Core itself refuses to build for anything else (ScribeValidateNativeRid).
                runtimeIdentifier = string.Empty;
                platform = Platform.AnyCpu;
                return false;
        }
    }

    /// <summary>
    /// The <c>dotnet.exe</c> belonging to the runtime this process is running on, or null when it
    /// cannot be found (a self-contained host, or an unusual layout). The shared framework lives at
    /// <c>&lt;dotnet root&gt;\shared\Microsoft.NETCore.App\&lt;version&gt;\</c>, and the host beside that
    /// root has the same architecture as the runtime, which a PATH lookup does not guarantee.
    /// </summary>
    public static string? FindDotNetHost()
    {
        try
        {
            var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
            var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
            var host = Path.Combine(root, "dotnet.exe");
            return File.Exists(host) ? host : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The platform-specific target framework of <paramref name="assembly"/>, such as
    /// <c>net10.0-windows10.0.19041.0</c>. BenchmarkDotNet derives the same value from the same two
    /// attributes when it picks its default toolchain; a custom toolchain has to state it.
    /// </summary>
    public static string? TargetFrameworkMoniker(Assembly assembly)
    {
        var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (string.IsNullOrEmpty(framework))
        {
            return null;
        }

        var version = new FrameworkName(framework).Version;
        var moniker = string.Create(CultureInfo.InvariantCulture, $"net{version.Major}.{version.Minor}");
        var platform = assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName;
        return string.IsNullOrEmpty(platform) ? moniker : $"{moniker}-{platform.ToLowerInvariant()}";
    }
}

/// <summary>
/// Reads the machine's active power plan. BenchmarkDotNet 0.15.8 switches the whole machine to the
/// High performance plan for a run unless the job opts out (<c>PowerManagementApplier</c>), and the
/// plan changes clock and parking behavior enough to move timings, so every environment line
/// carries it.
/// </summary>
internal static class PowerPlanProbe
{
    /// <summary>The plan's name and GUID, such as <c>Balanced {381b4222-...}</c>, or "unknown".</summary>
    public static string Describe()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var scheme) != 0 || scheme == IntPtr.Zero)
            {
                return "unknown";
            }

            try
            {
                var id = Marshal.PtrToStructure<Guid>(scheme).ToString("B", CultureInfo.InvariantCulture);
                var name = ReadFriendlyName(scheme);
                return string.IsNullOrWhiteSpace(name) ? id : $"{name} {id}";
            }
            finally
            {
                // The GUID is allocated by the API and documented as the caller's to LocalFree.
                _ = LocalFree(scheme);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "unknown";
        }
    }

    /// <summary>The plan's name without its GUID, safe for the marker line's column separator.</summary>
    public static string FriendlyPart(string plan)
    {
        var brace = plan.IndexOf(" {", StringComparison.Ordinal);
        return (brace > 0 ? plan[..brace] : plan).Replace('|', '/');
    }

    // Asked for the size first: the name is a Unicode string of a length only the API knows.
    private static string? ReadFriendlyName(IntPtr scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
