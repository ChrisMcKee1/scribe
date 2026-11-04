using System.Globalization;
using System.Text;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Encodes an HTML fragment as the clipboard's registered "HTML Format" (CF_HTML): a UTF-8 description header whose
/// StartHTML, EndHTML, StartFragment and EndFragment give byte offsets into the data, then the HTML with the fragment
/// between the StartFragment and EndFragment comments. See
/// <see href="https://learn.microsoft.com/windows/win32/dataxchg/html-clipboard-format"/>.
/// </summary>
internal static class ClipboardHtmlFormat
{
    /// <summary>The registered clipboard format name.</summary>
    internal const string FormatName = "HTML Format";

    private const string Prefix = "<html><body>\r\n<!--StartFragment-->";
    private const string Suffix = "<!--EndFragment-->\r\n</body></html>";

    // Every offset is written with ten digits, so the header's length does not depend on the offsets in it.
    private const string HeaderTemplate =
        "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

    // Strict: a fragment holding a lone surrogate gets no HTML rather than a replacement character the text copy lacks.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly int HeaderBytes =
        StrictUtf8.GetByteCount(string.Format(CultureInfo.InvariantCulture, HeaderTemplate, 0, 0, 0, 0));

    /// <summary>
    /// The CF_HTML data for <paramref name="fragment"/>, ending in one NUL that no offset counts; null when the fragment
    /// cannot be encoded as UTF-8.
    /// </summary>
    public static byte[]? Encode(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        int fragmentBytes;
        try
        {
            fragmentBytes = StrictUtf8.GetByteCount(fragment);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }

        int startHtml = HeaderBytes;
        int startFragment = startHtml + Encoding.ASCII.GetByteCount(Prefix);
        int endFragment = startFragment + fragmentBytes;
        int endHtml = endFragment + Encoding.ASCII.GetByteCount(Suffix);
        string header = string.Format(CultureInfo.InvariantCulture, HeaderTemplate, startHtml, endHtml, startFragment, endFragment);

        var data = new byte[endHtml + 1];
        int written = StrictUtf8.GetBytes(header, data);
        written += StrictUtf8.GetBytes(Prefix, data.AsSpan(written));
        written += StrictUtf8.GetBytes(fragment, data.AsSpan(written));
        StrictUtf8.GetBytes(Suffix, data.AsSpan(written));
        return data;
    }
}
