namespace Scribe.Core.Cleanup;

/// <summary>
/// Maps ONNX Runtime execution provider names to the device type Microsoft documents for each one,
/// so the settings UI can say whether a model runs on the CPU, GPU or NPU.
/// <para>
/// This is presentation only. Foundry Local performs hardware detection and picks the provider
/// itself ("the Core API automatically identifies available hardware and chooses the best execution
/// provider for each model"), so Scribe reports that choice rather than trying to influence it.
/// Provider names and device types come from the Foundry Local architecture reference:
/// https://learn.microsoft.com/azure/foundry-local/concepts/foundry-local-architecture
/// </para>
/// </summary>
public static class FoundryExecutionProviders
{
    public const string Cpu = "CPUExecutionProvider";

    /// <summary>Device type for a provider name, or null when the provider is unknown to us.</summary>
    public static string? DeviceType(string? executionProvider)
    {
        if (string.IsNullOrWhiteSpace(executionProvider))
        {
            return null;
        }

        // Matched on a contains basis so a provider we have not seen before still lands in the right
        // bucket, and an unrecognised one is reported honestly rather than guessed at.
        var name = executionProvider.Trim();

        if (name.Contains("QNN", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Vitis", StringComparison.OrdinalIgnoreCase))
        {
            return "NPU";
        }

        if (name.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TensorRT", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WebGpu", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DML", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        return name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? "CPU" : null;
    }

    /// <summary>Sentence for the model picker, or null when the provider is unknown.</summary>
    public static string? Describe(string? executionProvider)
    {
        var device = DeviceType(executionProvider);
        if (device is null)
        {
            return null;
        }

        var friendly = Friendly(executionProvider!);
        return device switch
        {
            "NPU" => $"Runs on the NPU ({friendly}), which is the most power efficient option.",
            "GPU" => $"Runs on the GPU ({friendly}).",
            _ => "Runs on the CPU, which works on any PC.",
        };
    }

    private static string Friendly(string executionProvider)
    {
        var name = executionProvider.Trim();
        return name.EndsWith("ExecutionProvider", StringComparison.OrdinalIgnoreCase)
            ? name[..^"ExecutionProvider".Length]
            : name;
    }
}
