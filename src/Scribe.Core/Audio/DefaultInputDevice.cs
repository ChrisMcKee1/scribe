using NAudio.CoreAudioApi;
using Scribe.Core.Models;

namespace Scribe.Core.Audio;

/// <summary>
/// What "the Windows default microphone" means to Scribe, and how the active capture endpoints are listed against it.
/// </summary>
/// <remarks>
/// <para>
/// Windows keeps one default capture endpoint per device role, and they can be different devices: "each role in the
/// table is assigned to one (and only one) rendering device and to one (and only one) capture device"
/// (https://learn.microsoft.com/windows/win32/coreaudio/device-roles). The Sound control panel sets the default
/// communication device separately, for "a device that is chosen by the user for handling phone calls"
/// (https://learn.microsoft.com/windows/win32/coreaudio/using-the-communication-device), while the device Windows
/// Settings shows under Sound, Input is the default device: on the machine that reported the bug, Settings showed the
/// device holding the console and multimedia roles while the communications role belonged to another one.
/// </para>
/// <para>
/// Dictation is the user talking to the computer, which is the console role's own capture example ("Voice commands";
/// the communications role is "Voice communications with another person"), so the console role is asked for first.
/// Scribe used to ask for the communications role first. On a machine whose defaults differ it recorded from the call
/// device while Windows Settings showed another one, and changing the default in Windows Settings never reached it,
/// restart or not. Multimedia, then communications, remain as fallbacks only: GetDefaultAudioEndpoint documents that
/// finding no device for a role means no capture device is available at all
/// (https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint),
/// so they cost nothing and change nothing on a working system.
/// </para>
/// </remarks>
internal static class DefaultInputDevice
{
    /// <summary>The roles asked for, in order, when Scribe follows the Windows default.</summary>
    public static IReadOnlyList<Role> RolePreference { get; } = [Role.Console, Role.Multimedia, Role.Communications];

    /// <summary>
    /// Reads the default capture endpoint of every role through <paramref name="defaultIdFor"/>, which returns null when
    /// Windows has none for that role.
    /// </summary>
    public static DefaultInputEndpoints Resolve(Func<Role, string?> defaultIdFor)
    {
        ArgumentNullException.ThrowIfNull(defaultIdFor);
        return new DefaultInputEndpoints(
            defaultIdFor(Role.Console),
            defaultIdFor(Role.Multimedia),
            defaultIdFor(Role.Communications));
    }

    /// <summary>
    /// The active endpoints as <see cref="AudioDevice"/>s, in the order given, with the default and the communications
    /// default flagged.
    /// </summary>
    public static IReadOnlyList<AudioDevice> Describe(IReadOnlyList<CaptureEndpoint> active, DefaultInputEndpoints defaults)
    {
        ArgumentNullException.ThrowIfNull(active);
        var resolved = defaults.ResolvedId;
        var devices = new List<AudioDevice>(active.Count);
        foreach (var endpoint in active)
        {
            devices.Add(new AudioDevice(
                endpoint.Id,
                endpoint.Name,
                IsDefault: IsSame(endpoint.Id, resolved),
                IsCommunicationsDefault: IsSame(endpoint.Id, defaults.CommunicationsId)));
        }

        return devices;
    }

    // Endpoint ID strings are opaque and compared exactly
    // (https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient).
    private static bool IsSame(string id, string? other) =>
        other is not null && string.Equals(id, other, StringComparison.Ordinal);
}

/// <summary>The default capture endpoint of each role, by endpoint ID string; null where Windows has none.</summary>
internal readonly record struct DefaultInputEndpoints(string? ConsoleId, string? MultimediaId, string? CommunicationsId)
{
    /// <summary>The endpoint Scribe records from when it follows Windows, by <see cref="DefaultInputDevice.RolePreference"/>.</summary>
    public string? ResolvedId => ResolvedRole is { } role ? IdFor(role) : null;

    /// <summary>The role <see cref="ResolvedId"/> came from; null when there is no capture device at all.</summary>
    public Role? ResolvedRole
    {
        get
        {
            foreach (var role in DefaultInputDevice.RolePreference)
            {
                if (IdFor(role) is not null)
                {
                    return role;
                }
            }

            return null;
        }
    }

    /// <summary>The default endpoint of one role.</summary>
    public string? IdFor(Role role) => role switch
    {
        Role.Console => ConsoleId,
        Role.Multimedia => MultimediaId,
        Role.Communications => CommunicationsId,
        _ => null,
    };
}
