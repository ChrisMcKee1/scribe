namespace Scribe.Core.TextInjection;

/// <summary>
/// The shape of a text Scribe inserts, as counts only (AGENTS.md, "Logging mandate"): its line breaks, counted the way
/// typing sends them (a CRLF is one Return, a lone CR or LF one each), its surrogate pairs (each a character outside the
/// Basic Multilingual Plane, typed as two Unicode events), and its other control characters (a tab, for example). What a
/// report about input a remote session could not take needs to know about the text, and nothing of what it says.
/// </summary>
internal readonly record struct InjectedTextShape(int LineBreaks, int SurrogatePairs, int ControlCharacters)
{
    public static InjectedTextShape Of(string text)
    {
        int lineBreaks = 0, surrogatePairs = 0, controlCharacters = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '\r' or '\n')
            {
                lineBreaks++;
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                surrogatePairs++;
                i++;
            }
            else if (char.IsControl(ch))
            {
                controlCharacters++;
            }
        }

        return new InjectedTextShape(lineBreaks, surrogatePairs, controlCharacters);
    }
}
