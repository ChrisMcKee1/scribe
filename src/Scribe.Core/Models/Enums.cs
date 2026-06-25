namespace Scribe.Core.Models;

/// <summary>How the dictation hotkey behaves.</summary>
public enum HotkeyMode
{
    /// <summary>Record only while the key is held; transcribe on release.</summary>
    Hold,

    /// <summary>First press starts recording, second press stops and transcribes.</summary>
    Toggle,
}

/// <summary>Modifier keys that may accompany a hotkey's primary key.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>Strategy used to place transcribed text into the focused application.</summary>
public enum InjectionMethod
{
    /// <summary>Set the clipboard and send Ctrl+V, then restore the prior clipboard.</summary>
    ClipboardPaste,

    /// <summary>
    /// Synthesize Unicode keystrokes directly. The most broadly compatible method (works even in
    /// paste-blocking fields) and the app default.
    /// </summary>
    UnicodeType,
}
