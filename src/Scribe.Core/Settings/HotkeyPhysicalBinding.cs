using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public static class HotkeyPhysicalBinding
{
    public static bool Same(HotkeyBinding? left, HotkeyBinding? right) =>
        left is not null &&
        right is not null &&
        left.VirtualKey == right.VirtualKey &&
        left.SecondaryVirtualKey == right.SecondaryVirtualKey &&
        left.Modifiers == right.Modifiers;
}
