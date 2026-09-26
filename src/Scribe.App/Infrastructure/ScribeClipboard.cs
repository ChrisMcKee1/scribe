using System.Windows;
using Scribe.Core.TextInjection;

namespace Scribe.App.Infrastructure;

internal static class ScribeClipboard
{
    public static bool SetText(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text ?? string.Empty, TextDataFormat.UnicodeText);
            foreach (var name in ClipboardPrivacyFormats.Names)
            {
                data.SetData(name, new byte[sizeof(uint)]);
            }

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
