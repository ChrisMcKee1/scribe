using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>What a microphone choice stands for.</summary>
public enum MicrophoneChoiceKind
{
    /// <summary>Follow the Windows default input device, whichever it is when a dictation starts.</summary>
    WindowsDefault,

    /// <summary>A specific microphone that is available now.</summary>
    Device,

    /// <summary>The saved microphone, which is not available now. Dictation falls back to the Windows default meanwhile.</summary>
    Unavailable,
}

/// <summary>A microphone as the user chose it: the endpoint ID string and the name it had, or neither for the Windows default.</summary>
/// <param name="DeviceId">The endpoint ID string, or null to follow the Windows default.</param>
/// <param name="DeviceName">The friendly name when it was chosen, so an unavailable microphone can still be named.</param>
public readonly record struct MicrophoneSelection(string? DeviceId, string? DeviceName)
{
    /// <summary>Follow the Windows default.</summary>
    public static MicrophoneSelection WindowsDefault { get; } = new(null, null);

    /// <summary>What <see cref="AppSettings"/> holds.</summary>
    public static MicrophoneSelection From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Normalize(settings.InputDeviceId, settings.InputDeviceName);
    }

    /// <summary>A blank ID means the Windows default, which never carries a name.</summary>
    public static MicrophoneSelection Normalize(string? deviceId, string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceId) ? WindowsDefault : new MicrophoneSelection(deviceId, deviceName);

    /// <summary>Writes this choice into <paramref name="settings"/>.</summary>
    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(DeviceId, DeviceName);
        settings.InputDeviceId = normalized.DeviceId;
        settings.InputDeviceName = normalized.DeviceName;
    }
}

/// <summary>One entry of a microphone picker.</summary>
/// <param name="Kind">What the entry stands for.</param>
/// <param name="Label">What the entry says.</param>
/// <param name="Selection">What choosing the entry saves.</param>
public sealed record MicrophoneChoice(MicrophoneChoiceKind Kind, string Label, MicrophoneSelection Selection)
{
    // A picker that draws Label through DisplayMemberPath still announces ToString to a screen reader, and a record's
    // default ToString reads out every property.
    public override string ToString() => Label;
}

/// <summary>The entries of a microphone picker, and which one is the current choice.</summary>
public sealed record MicrophoneMenu(IReadOnlyList<MicrophoneChoice> Choices, int SelectedIndex)
{
    /// <summary>The entry that is the current choice.</summary>
    public MicrophoneChoice Selected => Choices[SelectedIndex];
}

/// <summary>
/// Builds the microphone picker for Settings and the tray from the devices Windows reports and the current choice. The
/// first entry follows the Windows default and names the device that means right now; every active microphone follows,
/// by name, with the one that is the Windows default marked; and a saved microphone that is not available is kept as an
/// entry of its own, still chosen, so opening the picker never quietly changes the choice.
/// </summary>
public static class MicrophoneChoices
{
    /// <summary>Marks the device that is the Windows default right now.</summary>
    public const string DefaultSuffix = " (default)";

    /// <summary>Builds the picker. <paramref name="devices"/> are the active input devices, in any order.</summary>
    public static MicrophoneMenu Build(IReadOnlyList<AudioDevice> devices, MicrophoneSelection current)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var windowsDefault = devices.FirstOrDefault(device => device.IsDefault);
        var choices = new List<MicrophoneChoice>(devices.Count + 2)
        {
            new(MicrophoneChoiceKind.WindowsDefault, WindowsDefaultLabel(windowsDefault?.Name), MicrophoneSelection.WindowsDefault),
        };

        foreach (var device in devices
                     .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(device => device.Id, StringComparer.Ordinal))
        {
            choices.Add(new MicrophoneChoice(
                MicrophoneChoiceKind.Device,
                device.IsDefault ? device.Name + DefaultSuffix : device.Name,
                new MicrophoneSelection(device.Id, device.Name)));
        }

        var chosen = MicrophoneSelection.Normalize(current.DeviceId, current.DeviceName);
        if (chosen.DeviceId is not { } chosenId)
        {
            return new MicrophoneMenu(choices, 0);
        }

        var index = choices.FindIndex(choice =>
            choice.Kind == MicrophoneChoiceKind.Device &&
            string.Equals(choice.Selection.DeviceId, chosenId, StringComparison.Ordinal));
        if (index >= 0)
        {
            return new MicrophoneMenu(choices, index);
        }

        choices.Add(new MicrophoneChoice(MicrophoneChoiceKind.Unavailable, UnavailableLabel(chosen.DeviceName), chosen));
        return new MicrophoneMenu(choices, choices.Count - 1);
    }

    /// <summary>The Windows default entry, naming the device it resolves to now.</summary>
    public static string WindowsDefaultLabel(string? defaultDeviceName) =>
        string.IsNullOrWhiteSpace(defaultDeviceName)
            ? "Windows default (no microphone found)"
            : $"Windows default: {defaultDeviceName}";

    /// <summary>The entry for a saved microphone that is not available now.</summary>
    public static string UnavailableLabel(string? deviceName) =>
        $"Unavailable: {(string.IsNullOrWhiteSpace(deviceName) ? "saved microphone" : deviceName)}";

    /// <summary>How a choice reads in a short status line, such as the tray tooltip.</summary>
    public static string Describe(MicrophoneSelection selection, IReadOnlyList<AudioDevice>? devices = null)
    {
        var chosen = MicrophoneSelection.Normalize(selection.DeviceId, selection.DeviceName);
        if (chosen.DeviceId is null)
        {
            return "the Windows default microphone";
        }

        var name = devices?.FirstOrDefault(device => string.Equals(device.Id, chosen.DeviceId, StringComparison.Ordinal))?.Name
            ?? chosen.DeviceName;
        return string.IsNullOrWhiteSpace(name) ? "the saved microphone" : name;
    }
}
