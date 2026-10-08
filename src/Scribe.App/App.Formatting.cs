using Scribe.Core.TextInjection;
using Scribe.Core.Tray;

namespace Scribe.App;

public partial class App
{
    private void OnPlainTextOnceChanged(PlainTextOnceState state) => Dispatcher.BeginInvoke(() =>
    {
        if (_controller?.IsClosing != false || _plainTextOnce is not { } owner ||
            state.Revision <= _shownPlainTextOnceRevision || state.Revision != owner.Current.Revision)
        {
            return;
        }

        _shownPlainTextOnceRevision = state.Revision;
        _tray?.SetPlainTextOnce(state);
        if (TrayNotices.PlainTextOnce(state) is { } notice)
        {
            ShowTrayNotice(notice);
        }
    });
}
