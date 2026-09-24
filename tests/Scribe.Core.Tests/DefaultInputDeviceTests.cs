using NAudio.CoreAudioApi;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// Which endpoint "the Windows default microphone" is: the console role (the device Windows Settings shows under Sound,
/// Input), then multimedia, then communications, which is for calls and can be another device entirely.
/// </summary>
public sealed class DefaultInputDeviceTests
{
    [Fact]
    public void The_console_role_is_asked_for_first_and_communications_last()
    {
        Assert.Equal([Role.Console, Role.Multimedia, Role.Communications], DefaultInputDevice.RolePreference);
    }

    [Theory]
    [InlineData("insta", "insta", "elgato", "insta", Role.Console)]
    [InlineData(null, "insta", "elgato", "insta", Role.Multimedia)]
    [InlineData(null, null, "elgato", "elgato", Role.Communications)]
    [InlineData(null, null, null, null, null)]
    public void The_default_is_the_first_role_windows_has_a_device_for(
        string? console, string? multimedia, string? communications, string? expected, Role? expectedRole)
    {
        var defaults = new DefaultInputEndpoints(console, multimedia, communications);

        Assert.Equal(expected, defaults.ResolvedId);
        Assert.Equal(expectedRole, defaults.ResolvedRole);
    }

    [Fact]
    public void Resolving_reads_every_role_once()
    {
        var asked = new List<Role>();

        var defaults = DefaultInputDevice.Resolve(role =>
        {
            asked.Add(role);
            return role == Role.Communications ? "elgato" : "insta";
        });

        Assert.Equal([Role.Console, Role.Multimedia, Role.Communications], asked);
        Assert.Equal(new DefaultInputEndpoints("insta", "insta", "elgato"), defaults);
    }

    [Fact]
    public void The_list_flags_the_default_and_the_communications_default_by_exact_id()
    {
        var active = new List<CaptureEndpoint>
        {
            new("{0.0.1.00000000}.{insta}", "Microphone (Insta360)"),
            new("{0.0.1.00000000}.{elgato}", "Mic In (Elgato)"),
            new("{0.0.1.00000000}.{ELGATO}", "A different endpoint whose ID differs only in case"),
        };

        var devices = DefaultInputDevice.Describe(
            active, new DefaultInputEndpoints("{0.0.1.00000000}.{insta}", null, "{0.0.1.00000000}.{elgato}"));

        Assert.Equal([true, false, false], devices.Select(device => device.IsDefault));
        Assert.Equal([false, true, false], devices.Select(device => device.IsCommunicationsDefault));
        Assert.Equal(active.Select(endpoint => endpoint.Name), devices.Select(device => device.Name));
    }
}
