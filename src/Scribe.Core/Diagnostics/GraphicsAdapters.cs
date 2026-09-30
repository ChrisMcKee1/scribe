using Microsoft.Win32;

namespace Scribe.Core.Diagnostics;

/// <summary>A display adapter Windows lists, with the memory it reports for itself.</summary>
/// <param name="Name">The driver's description, such as "NVIDIA GeForce RTX 5080".</param>
/// <param name="DedicatedBytes">
/// The adapter's own memory as its driver reports it (HardwareInformation.qwMemorySize), 0 when it reports none. An
/// integrated graphics chip reports only the small share of system memory set aside for it.
/// </param>
public sealed record GraphicsAdapter(string Name, long DedicatedBytes)
{
    private const long Gib = 1024L * 1024 * 1024;

    /// <summary>
    /// An NVIDIA RTX card, the one kind of graphics card Foundry Local serves its Qwen2.5 models on through TensorRT for
    /// RTX, as measured in the 0.5.2 local benchmark (docs/local-model-benchmark.md).
    /// </summary>
    public bool IsNvidiaRtx =>
        Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        Name.Contains("RTX", StringComparison.OrdinalIgnoreCase);

    /// <summary>At least <paramref name="gib"/> GiB of its own memory.</summary>
    public bool HasAtLeast(int gib) => DedicatedBytes >= gib * Gib;
}

/// <summary>
/// The display adapters on this PC, read from the registry keys every display driver fills (the display adapter class,
/// {4d36e968-e325-11ce-bfc1-08002be10318}). What AI cleanup's defaults on this PC are chosen from: a model that fits a
/// graphics card with room to spare runs well there and badly without one.
/// </summary>
public static class GraphicsAdapters
{
    private const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Every adapter that reports a description. Never throws; empty when nothing can be read.</summary>
    public static IReadOnlyList<GraphicsAdapter> Detect()
    {
        var adapters = new List<GraphicsAdapter>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(ClassKey);
            if (classKey is null)
            {
                return adapters;
            }

            foreach (var name in classKey.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit))
                {
                    continue;
                }

                try
                {
                    using var adapter = classKey.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is not string description || string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    adapters.Add(new GraphicsAdapter(description.Trim(), MemoryOf(adapter)));
                }
                catch (Exception)
                {
                    // One adapter's key that cannot be read leaves the others.
                }
            }
        }
        catch (Exception)
        {
            // No access to the class key: nothing is known, which reads as a PC without a graphics card.
        }

        return adapters;
    }

    // The 64-bit value where the driver writes one; the older 32-bit value tops out at 4 GB, so it only stands in.
    private static long MemoryOf(RegistryKey adapter) => adapter.GetValue("HardwareInformation.qwMemorySize") switch
    {
        long qword when qword > 0 => qword,
        byte[] { Length: >= 8 } bytes => Math.Max(0, BitConverter.ToInt64(bytes, 0)),
        _ => adapter.GetValue("HardwareInformation.MemorySize") switch
        {
            int dword => (uint)dword,
            byte[] { Length: >= 4 } bytes => BitConverter.ToUInt32(bytes, 0),
            _ => 0,
        },
    };
}
