using System.Runtime.InteropServices;
using System.Text;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Enumerates neural processing units Windows has installed, via the SetupAPI device tree.
///
/// Windows groups NPUs under the <c>ComputeAccelerator</c> setup class, which every vendor
/// (Qualcomm Hexagon, Intel AI Boost, AMD XDNA) registers into. The class itself exists on machines
/// with no NPU at all, so an empty result is the normal, expected answer on most PCs and never an
/// error. Every failure path returns "no accelerators" rather than throwing: this is a diagnostic
/// hint and must never be able to interfere with dictation.
/// </summary>
internal static class NeuralAcceleratorProbe
{
    // Device setup class GUID for ComputeAccelerator, the class Windows files NPUs under.
    private static readonly Guid ComputeAcceleratorClass = new("f01a9d53-3ff6-48d2-9f97-c8a7004be10c");

    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint MaxPropertyBytes = 8192;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static IReadOnlyList<NeuralAccelerator> Enumerate()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        try
        {
            return EnumerateCore();
        }
        catch
        {
            // SetupAPI is unavailable or refused us. Report "no NPU" and carry on; the CPU decode
            // path is unaffected either way.
            return [];
        }
    }

    private static IReadOnlyList<NeuralAccelerator> EnumerateCore()
    {
        var classGuid = ComputeAcceleratorClass;
        var set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DigcfPresent);
        if (set == InvalidHandle || set == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var found = new List<NeuralAccelerator>();
            var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };

            for (uint index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    // Any terminating condition ends the walk. ERROR_NO_MORE_ITEMS is the expected
                    // one; a genuine failure is indistinguishable here and would only mean we
                    // report fewer accelerators, which is the safe direction for a diagnostic.
                    break;
                }

                // FriendlyName is what the user would see in Device Manager, but drivers are only
                // required to supply DeviceDesc, so fall back to it.
                var name = ReadProperty(set, ref info, SpdrpFriendlyName)
                           ?? ReadProperty(set, ref info, SpdrpDeviceDesc);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    found.Add(new NeuralAccelerator(name, ComputeCapabilityReport.ClassifyVendor(name)));
                }
            }

            return found;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? ReadProperty(IntPtr set, ref SpDevInfoData info, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, null, 0, out var required);

        // A device name is a short REG_SZ. The cap is a sanity bound on a value the driver
        // controls, not an API limit: anything larger is not a name we would want to display, and
        // refusing it keeps a malformed property from driving a large allocation.
        if (required == 0 || required > MaxPropertyBytes)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, required, out _))
        {
            return null;
        }

        // REG_SZ from SetupAPI is UTF-16 and includes its terminating null.
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim() is { Length: > 0 } text
            ? text
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
