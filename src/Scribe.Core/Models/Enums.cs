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

/// <summary>
/// What happens to line breaks in dictated text before it is injected. Terminals treat a typed or
/// pasted newline as Enter, so a multi-paragraph dictation can submit a half-finished command or
/// chat message; flattening replaces line breaks with spaces to keep the input as one line.
/// </summary>
public enum NewlineInjectionMode
{
    /// <summary>Flatten line breaks only when the focused app is a known terminal (default).</summary>
    SmartFlatten,

    /// <summary>Always replace line breaks with spaces, in every app.</summary>
    AlwaysFlatten,

    /// <summary>Inject text exactly as produced, line breaks included.</summary>
    KeepNewlines,
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
