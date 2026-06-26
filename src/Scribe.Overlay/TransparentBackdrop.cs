using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Scribe.Overlay.Interop;
using Scribe.Overlay.Logging;
using Windows.UI;
using WinComp = Windows.UI.Composition;

namespace Scribe.Overlay;

/// <summary>
/// A custom <see cref="SystemBackdrop"/> that makes the window background fully transparent by
/// filling the system-backdrop region with an alpha-0 composition color brush. This is the modern,
/// DWM-composition transparency path for WinUI 3 (Windows App SDK 2.x) — deliberately NOT the WPF
/// AllowsTransparency / UpdateLayeredWindow path that produced the recurring black box.
///
/// The <c>ICompositionSupportsSystemBackdrop.SystemBackdrop</c> property is a
/// <c>Windows.UI.Composition.CompositionBrush</c>, so the brush MUST come from a system
/// <c>Windows.UI.Composition.Compositor</c> (which requires a Windows.System.DispatcherQueue on the
/// thread), not the Microsoft.UI element compositor.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private readonly WindowsSystemDispatcherQueueHelper _dispatcherQueueHelper = new();
    private WinComp.Compositor? _compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        try
        {
            _dispatcherQueueHelper.EnsureDispatcherQueueController();
            _compositor ??= new WinComp.Compositor();
            connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
            OverlayLog.Write("TransparentBackdrop.OnTargetConnected applied alpha-0 system backdrop brush");
        }
        catch (Exception ex)
        {
            OverlayLog.Error("TransparentBackdrop.OnTargetConnected failed", ex);
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        OverlayLog.Write("TransparentBackdrop.OnTargetDisconnected cleared backdrop brush");
    }
}
