namespace Scribe.Core.Cleanup;

/// <summary>
/// Turns the hardware the Foundry Local SDK reports for a model into a sentence for the settings UI.
/// <para>
/// Presentation only. The SDK classifies the device itself and Foundry Local performs the hardware
/// detection ("the Core API automatically identifies available hardware and chooses the best
/// execution provider for each model"), so this never re-derives that decision. Under the WinML
/// package the provider set is extended by Windows Update, so trusting the SDK's own device type is
/// what keeps a provider we have never seen classified correctly.
/// https://learn.microsoft.com/azure/foundry-local/concepts/foundry-local-architecture
/// </para>
/// </summary>
public static class FoundryExecutionProviders
{
    /// <summary>
    /// Sentence for the model picker, or null when the SDK reports no usable device. The device
    /// type comes from the SDK; the provider name only supplies the parenthetical detail.
    /// </summary>
    public static string? Describe(string? deviceType, string? executionProvider)
    {
        var device = deviceType?.Trim();
        if (string.IsNullOrEmpty(device))
        {
            return null;
        }

        // "Invalid" is the SDK's own value for "no meaningful device", so it has to read as silence
        // rather than as a hardware claim.
        if (device.Equals("Invalid", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var detail = Friendly(executionProvider);
        var parenthetical = string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})";

        if (device.Equals("NPU", StringComparison.OrdinalIgnoreCase))
        {
            return $"Runs on the NPU{parenthetical}, which is the most power efficient option.";
        }

        if (device.Equals("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return $"Runs on the GPU{parenthetical}.";
        }

        if (device.Equals("CPU", StringComparison.OrdinalIgnoreCase))
        {
            return "Runs on the CPU, which works on any PC.";
        }

        // A device type we do not recognise is still reported, because the SDK stating one means it
        // is real. Inventing a category for it, or staying silent, would both be worse than saying
        // exactly what was reported.
        return $"Runs on {device}{parenthetical}.";
    }

    private static string Friendly(string? executionProvider)
    {
        var name = executionProvider?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        return name.EndsWith("ExecutionProvider", StringComparison.OrdinalIgnoreCase)
            ? name[..^"ExecutionProvider".Length]
            : name;
    }
}
