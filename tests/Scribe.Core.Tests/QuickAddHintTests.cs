using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddHintTests
{
    [Theory]
    [InlineData(-1, -1, false, true, false, "Select the words Scribe got wrong. To pick several, click the next word, drag across them or Shift+click.")]
    [InlineData(1, 1, false, true, false, "To add the next word, click it. To start again, click this word.")]
    [InlineData(1, 2, false, true, false, "Click a word just before or after the selection to add it, or an end word to remove it.")]
    [InlineData(1, 2, true, true, false, "Arrow keys move between words. Space selects a word. Shift and an arrow key select several.")]
    [InlineData(-1, -1, false, false, false, "No recent dictations to pick from. Type the words in the boxes below.")]
    [InlineData(1, 2, false, true, true, "Your correction is still here. Try again before saving.")]
    public void Each_state_has_exact_text(int first, int last, bool keyboard, bool hasDictation, bool referencesUnavailable, string expected)
    {
        Assert.Equal(expected, QuickAddHint.For(new QuickDictionaryAdd.WordRange(first, last), keyboard, hasDictation, referencesUnavailable));
    }
}
