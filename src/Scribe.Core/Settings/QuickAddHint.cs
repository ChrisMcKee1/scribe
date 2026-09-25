namespace Scribe.Core.Settings;

public static class QuickAddHint
{
    public static string For(QuickDictionaryAdd.WordRange range, bool keyboardFocusInWords, bool hasDictation)
    {
        if (!hasDictation) return "No recent dictations to pick from. Type the words in the boxes below.";
        if (keyboardFocusInWords) return "Arrow keys move between words. Space selects a word. Shift and an arrow key select several.";
        if (range.IsEmpty) return "Select the words Scribe got wrong. To pick several, click the next word, drag across them or Shift+click.";
        return range.First == range.Last
            ? "To add the next word, click it. To start again, click this word."
            : "Click a word just before or after the selection to add it, or an end word to remove it.";
    }
}
