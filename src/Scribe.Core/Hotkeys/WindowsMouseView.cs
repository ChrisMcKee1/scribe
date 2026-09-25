namespace Scribe.Core.Hotkeys;

/// <summary>
/// Windows' own view of the middle and side mouse buttons, as the mouse hook callback reads it for a release whose press
/// this engine swallowed (<see cref="HotkeyEngine.OnMouseButtonEvent"/>). Both reads run inside the callback, so both
/// wait for nothing, take no lock of Scribe's and allocate nothing.
/// </summary>
/// <param name="IsDown">
/// GetAsyncKeyState's high bit for the button: true is Windows' own "down"; false is up, or a read that failed, which
/// returns zero too.
/// </param>
/// <param name="Reading">
/// The same read, told apart from a failure: true for down, false for up when nothing could have made the read fail, and
/// null for a zero that cannot be trusted (<see cref="NativeMethods.ReadMouseButtonState"/>).
/// </param>
internal sealed record WindowsMouseView(Func<uint, bool> IsDown, Func<uint, bool?> Reading)
{
    /// <summary>The real view, from user32 through <see cref="NativeMethods"/>.</summary>
    public static WindowsMouseView Native { get; } =
        new(NativeMethods.IsKeyLogicallyDown, NativeMethods.MouseButtonStateInWindows);
}
