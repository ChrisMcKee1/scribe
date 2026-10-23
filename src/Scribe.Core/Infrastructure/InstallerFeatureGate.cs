using Scribe.Core.Diagnostics;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Gates experimental and preview features by installer channel (<see cref="WindowsPackageIdentity.IsPackaged"/>)
/// and <see cref="PerfFlags"/>.
/// <para>
/// Microsoft Store packages must stay compliant with Store certification rules (for example, Microsoft Learn notes
/// that experimental Windows AI Speech APIs are not supported for Microsoft Store publishing, and the Store package
/// keeps speech recognition strictly on this PC by default). Direct downloads (Velopack installer and portable zip)
/// can use preview cloud speech recognition directly, and experimental NPU speech recognition when enabled by flag.
/// </para>
/// </summary>
public static class InstallerFeatureGate
{
    /// <summary>
    /// True when MAI-Transcribe-2 cloud speech recognition may be selected and used.
    /// Direct installs allow it out of the box; packaged Microsoft Store installs keep it off unless
    /// <see cref="PerfFlags.MaiTranscribeSpeech"/> is set in <c>SCRIBE_PERF_FLAGS</c>.
    /// </summary>
    public static bool CanUseMaiTranscribe(bool isPackaged, PerfFlags? flags = null)
    {
        var active = flags ?? PerfFlags.None;
        return !isPackaged || active.IsOn(PerfFlags.MaiTranscribeSpeech);
    }

    /// <summary>
    /// True when experimental Copilot+ PC NPU speech recognition is permitted.
    /// Never enabled in Microsoft Store packages (<paramref name="isPackaged"/> is true); on direct installs it
    /// requires both <see cref="PerfFlags.CopilotPlusNpuSpeech"/> and a native Copilot+ PC NPU.
    /// </summary>
    public static bool CanUseExperimentalCopilotPlusNpuSpeech(
        bool isPackaged,
        PerfFlags? flags,
        ComputeCapabilityReport capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (isPackaged)
        {
            return false;
        }

        var active = flags ?? PerfFlags.None;
        return active.IsOn(PerfFlags.CopilotPlusNpuSpeech) && capability.IsCopilotPlusNpu;
    }
}
