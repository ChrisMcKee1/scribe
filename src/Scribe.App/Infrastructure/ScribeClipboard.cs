using System.IO;
using System.Windows;
using Scribe.Core.TextInjection;

namespace Scribe.App.Infrastructure;

internal static class ScribeClipboard
{
    public static bool SetText(string text)
    {
        var streams = new List<MemoryStream>();
        try
        {
            var data = new DataObject();
            data.SetText(text ?? string.Empty, TextDataFormat.UnicodeText);
            foreach (var name in ClipboardPrivacyFormats.Names)
            {
                var marker = new MemoryStream(new byte[sizeof(uint)], writable: false);
                streams.Add(marker);
                data.SetData(name, marker, false);
            }

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}

