using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

public class HotkeyServiceTests
{
    [Fact]
    public void Start_installs_hook_and_Stop_removes_it()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        service.Start();
        Assert.True(service.IsRunning);

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void Start_is_idempotent_and_Dispose_is_safe()
    {
        var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        service.Start();
        service.Start(); // second call is a no-op
        Assert.True(service.IsRunning);

        service.Dispose();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void UpdateBinding_replaces_active_binding()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        Assert.Equal(HotkeyBinding.DefaultVirtualKey, service.Binding.VirtualKey);

        var toggle = new HotkeyBinding(0x20, KeyModifiers.Control, HotkeyMode.Toggle, Suppress: false, "Ctrl+Space");
        service.UpdateBinding(toggle);

        Assert.Equal(0x20u, service.Binding.VirtualKey);
        Assert.Equal(HotkeyMode.Toggle, service.Binding.Mode);
        Assert.Equal(KeyModifiers.Control, service.Binding.Modifiers);
    }
}
