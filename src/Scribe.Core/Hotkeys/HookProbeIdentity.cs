using System.Security.Cryptography;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Hotkeys;

/// <summary>A service-owned watchdog identity; default retains the shared legacy probe.</summary>
internal readonly struct HookProbeIdentity(nuint marker)
{
    public bool IsLocal => marker != 0;
    public nuint Marker => IsLocal ? marker : SyntheticInputMarker.Value;

    public bool IsOwn(uint key, bool isUp, nuint extraInfo) =>
        key == NativeMethods.VK_PROBE && isUp &&
        (IsLocal ? extraInfo == marker || extraInfo == (nuint)(uint)marker : KeyboardHookFilter.IsScribesOwn(extraInfo));

    public static HookProbeIdentity Create(PerfFlags flags)
    {
        if (!flags.IsOn(PerfFlags.ServiceLocalHookProbe))
        {
            return default;
        }

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        nuint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = (nuint)BitConverter.ToUInt64(bytes);
        }
        while ((uint)value == 0 || (uint)value == (uint)SyntheticInputMarker.Value ||
            (nuint.Size == sizeof(ulong) && value == (nuint)(uint)value));

        return new HookProbeIdentity(value);
    }
}
