namespace Scribe.Core.Persistence;

/// <summary>A setting the tray can change while a Settings window is open, each with its own record of intent.</summary>
public enum ExternalSetting
{
    /// <summary>The AI cleanup switch (<see cref="Scribe.Core.Models.AppSettings.EnableAiCleanup"/>).</summary>
    AiCleanup,

    /// <summary>
    /// The microphone (<see cref="Scribe.Core.Models.AppSettings.InputDeviceId"/> with
    /// <see cref="Scribe.Core.Models.AppSettings.InputDeviceName"/>).
    /// </summary>
    Microphone,
}

/// <summary>
/// A settings window's newest intent for each setting the tray can change: the revision of the newest change it holds
/// for that setting (the user's own, or a tray change it took), or zero when it holds none. See
/// <see cref="Scribe.Core.Settings.ExternalChoiceSync{T}.NewestRevision"/>.
/// </summary>
public readonly record struct ExternalIntents(long AiCleanup, long Microphone);
