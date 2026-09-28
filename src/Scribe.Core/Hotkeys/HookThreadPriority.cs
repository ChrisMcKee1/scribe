using System.Security;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Hotkeys;

internal static class HookThreadPriority
{
    public static ThreadPriority Select(PerfFlags flags) =>
        flags.IsOn(PerfFlags.HookPriorityAboveNormal) ? ThreadPriority.AboveNormal : ThreadPriority.Normal;

    public static void Apply(PerfFlags flags, Action<ThreadPriority> setPriority, ILogger logger)
    {
        if (Select(flags) == ThreadPriority.Normal)
        {
            return;
        }

        try
        {
            setPriority(ThreadPriority.AboveNormal);
        }
        catch (Exception ex) when (ex is ThreadStateException or SecurityException or PlatformNotSupportedException)
        {
            try
            {
                logger.LogWarning("Hook thread priority could not be changed ({Failure}); keeping the thread's existing priority.",
                    FailureShape.Describe(ex));
            }
            catch (Exception)
            {
                // Diagnostics do not decide whether a hook starts.
            }
        }
    }
}
