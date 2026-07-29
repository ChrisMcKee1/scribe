using System.Runtime.InteropServices;

namespace Scribe.Core.Diagnostics;

/// <summary>Which silicon vendor supplied a detected neural accelerator.</summary>
public enum AcceleratorVendor
{
    Unknown,
    Qualcomm,
    Intel,
    Amd,
}

/// <summary>A neural processing unit reported by Windows under the ComputeAccelerator class.</summary>
/// <param name="Name">The device description exactly as Windows reports it.</param>
/// <param name="Vendor">Best-effort vendor classification derived from <paramref name="Name"/>.</param>
public sealed record NeuralAccelerator(string Name, AcceleratorVendor Vendor);

/// <summary>
/// What the machine underneath Scribe can actually do: the architecture we were built for, the
/// architecture we are running on, and any NPU Windows knows about.
///
/// Scribe decodes on the CPU on every machine. This report exists to explain the environment in
/// diagnostics and to catch the one genuinely actionable case: an x64 build running under emulation
/// on an Arm64 PC, which works but is measurably slower and burns more battery than the native
/// build. NPU presence is recorded but does not change how anything runs; the Hexagon port of
/// Parakeet is not faster than CPU for push-to-talk length audio.
/// </summary>
public sealed record ComputeCapabilityReport
{
    private ComputeCapabilityReport(
        Architecture processArchitecture,
        Architecture osArchitecture,
        IReadOnlyList<NeuralAccelerator> accelerators)
    {
        ProcessArchitecture = processArchitecture;
        OsArchitecture = osArchitecture;
        Accelerators = accelerators;
    }

    public Architecture ProcessArchitecture { get; }

    public Architecture OsArchitecture { get; }

    public IReadOnlyList<NeuralAccelerator> Accelerators { get; }

    /// <summary>True when the process architecture differs from the OS, i.e. Windows is translating.</summary>
    public bool IsEmulated => ProcessArchitecture != OsArchitecture;

    /// <summary>True when this is a native Arm64 build on an Arm64 OS.</summary>
    public bool IsArm64Native =>
        ProcessArchitecture == Architecture.Arm64 && OsArchitecture == Architecture.Arm64;

    public bool HasNpu => Accelerators.Count > 0;

    /// <summary>
    /// A single actionable sentence for the user, or <c>null</c> when nothing needs saying.
    /// Only emulation warrants advice: everything else is working as intended.
    /// </summary>
    public string? Recommendation =>
        IsEmulated && OsArchitecture == Architecture.Arm64
            ? "Scribe is running under emulation. Install the Arm64 build for faster dictation and longer battery life."
            : null;

    /// <summary>
    /// Builds a report from already-probed values. Pure, so the interesting combinations
    /// (emulated, native Arm64, NPU present, NPU absent) are all directly testable.
    /// </summary>
    public static ComputeCapabilityReport Create(
        Architecture processArchitecture,
        Architecture osArchitecture,
        IEnumerable<NeuralAccelerator>? accelerators = null)
    {
        var list = accelerators is null
            ? []
            : accelerators
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .ToArray();

        return new ComputeCapabilityReport(processArchitecture, osArchitecture, list);
    }

    /// <summary>Probes the current machine. Never throws.</summary>
    public static ComputeCapabilityReport Detect() => Create(
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.OSArchitecture,
        NeuralAcceleratorProbe.Enumerate());

    /// <summary>A one-line summary suitable for the log and the diagnostics panel.</summary>
    public string Describe()
    {
        var npu = HasNpu
            ? string.Join(", ", Accelerators.Select(a => a.Name))
            : "none detected";

        var mode = IsEmulated
            ? $"{Format(ProcessArchitecture)} emulated on {Format(OsArchitecture)}"
            : $"{Format(ProcessArchitecture)} native";

        return $"CPU decode; {mode}; NPU: {npu}";
    }

    private static string Format(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "Arm64",
        Architecture.X86 => "x86",
        _ => architecture.ToString(),
    };

    /// <summary>
    /// Classifies a Windows device description. Matching is done on vendor tokens rather than full
    /// product names because those change every silicon generation (Hexagon, AI Boost, XDNA) while
    /// the vendor token does not.
    /// </summary>
    public static AcceleratorVendor ClassifyVendor(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return AcceleratorVendor.Unknown;

        if (Contains(deviceName, "qualcomm") || Contains(deviceName, "hexagon") || Contains(deviceName, "snapdragon"))
        {
            return AcceleratorVendor.Qualcomm;
        }

        if (Contains(deviceName, "intel") || Contains(deviceName, "ai boost") || Contains(deviceName, "movidius"))
        {
            return AcceleratorVendor.Intel;
        }

        if (Contains(deviceName, "amd") || Contains(deviceName, "xdna") || Contains(deviceName, "ryzen ai"))
        {
            return AcceleratorVendor.Amd;
        }

        return AcceleratorVendor.Unknown;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
