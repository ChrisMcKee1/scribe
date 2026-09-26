namespace Scribe.Core.Overlay;

/// <summary>
/// When a new text scale reaches the pill's window, which never changes size mid-fade. The scale is read at each show and
/// again whenever Windows reports a text size change. A pill that is appearing is still hidden, so it takes the scale
/// before its first frame; one on screen takes it at once, resized and re-anchored, unless it is fading in, when the change
/// waits for the fade to finish; one that is hidden or fading out takes it at its next show, which reads it again. The
/// overlay's UI thread is the only caller. No WinUI in it: the overlay compiles this file itself through a linked Compile
/// item, as it does <see cref="PillGeometry"/>.
/// </summary>
public sealed class PillTextScale
{
    private double? _pending;

    /// <summary>The scale the window is sized for.</summary>
    public double Current { get; private set; } = PillGeometry.MinTextScale;

    /// <summary>
    /// A show read <paramref name="textScale"/> (from <see cref="PillGeometry.TextScale"/>). The window is sized for
    /// <see cref="Current"/> right after, so a show while the pill fades in keeps its size and the change waits.
    /// </summary>
    public void OnShow(double textScale, bool appearing, bool fadingIn)
    {
        if (fadingIn && !appearing)
        {
            Defer(textScale);
            return;
        }

        Take(textScale);
    }

    /// <summary>
    /// Windows reported a text size change and <paramref name="textScale"/> was read again. Returns true when the window
    /// must be resized and re-anchored now.
    /// </summary>
    public bool OnChanged(double textScale, bool visible, bool fadingIn, bool fadingOut)
    {
        if (!visible || fadingOut)
        {
            _pending = null; // the next show reads it again
            return false;
        }

        if (fadingIn)
        {
            Defer(textScale);
            return false;
        }

        return Take(textScale);
    }

    /// <summary>The fade in finished. Returns true when a change it held back must be applied now.</summary>
    public bool OnFadeInCompleted(bool visible, bool fadingOut)
    {
        if (_pending is not { } textScale)
        {
            return false;
        }

        _pending = null;
        return visible && !fadingOut && Take(textScale);
    }

    /// <summary>The pill started to hide: a change held back is dropped, since the next show reads the scale again.</summary>
    public void OnHidden() => _pending = null;

    private void Defer(double textScale) => _pending = textScale;

    private bool Take(double textScale)
    {
        _pending = null;
        if (textScale == Current)
        {
            return false;
        }

        Current = textScale;
        return true;
    }
}
