using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Covers the plain-click selection model behind the quick add popup's word chips. The rules are
/// worth pinning down because the first shipped build put phrase building behind shift-click and a
/// drag, which read to the user as multi-select being missing.
/// </summary>
public class QuickDictionaryAddSelectionTests
{
    private static QuickDictionaryAdd.WordRange Range(int first, int last) => new(first, last);

    [Fact]
    public void Toggle_FromEmpty_SelectsSingleWord()
    {
        var result = QuickDictionaryAdd.Toggle(QuickDictionaryAdd.WordRange.None, 4);

        Assert.Equal(Range(4, 4), result);
    }

    [Fact]
    public void Toggle_NextWord_ExtendsRight()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 4), 5);

        Assert.Equal(Range(4, 5), result);
    }

    [Fact]
    public void Toggle_PreviousWord_ExtendsLeft()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 6), 3);

        Assert.Equal(Range(3, 6), result);
    }

    /// <summary>The reported case: three chips joined into one pattern with three plain clicks.</summary>
    [Fact]
    public void Toggle_ThreeAdjacentClicks_BuildsThreeWordPhrase()
    {
        var range = QuickDictionaryAdd.Toggle(QuickDictionaryAdd.WordRange.None, 10);
        range = QuickDictionaryAdd.Toggle(range, 11);
        range = QuickDictionaryAdd.Toggle(range, 12);

        Assert.Equal(Range(10, 12), range);
    }

    [Fact]
    public void Toggle_FirstWordOfPhrase_ShrinksFromTheLeft()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 7), 4);

        Assert.Equal(Range(5, 7), result);
    }

    [Fact]
    public void Toggle_LastWordOfPhrase_ShrinksFromTheRight()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 7), 7);

        Assert.Equal(Range(4, 6), result);
    }

    [Fact]
    public void Toggle_MiddleOfPhrase_CollapsesToThatWord()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 8), 6);

        Assert.Equal(Range(6, 6), result);
    }

    [Fact]
    public void Toggle_OnlySelectedWord_ClearsSelection()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 4), 4);

        Assert.True(result.IsEmpty);
    }

    /// <summary>
    /// A distant click starts over. Spanning to it would silently select every word in between,
    /// none of which the user pointed at.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(40)]
    public void Toggle_WordAwayFromPhrase_StartsAgain(int index)
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 7), index);

        Assert.Equal(Range(index, index), result);
    }

    [Fact]
    public void Toggle_ExtendThenShrink_ReturnsToTheEarlierPhrase()
    {
        var range = QuickDictionaryAdd.Toggle(QuickDictionaryAdd.WordRange.None, 5);
        range = QuickDictionaryAdd.Toggle(range, 6);
        range = QuickDictionaryAdd.Toggle(range, 4);
        Assert.Equal(Range(4, 6), range);

        range = QuickDictionaryAdd.Toggle(range, 4);
        Assert.Equal(Range(5, 6), range);
    }

    /// <summary>
    /// Clicking every word of a phrase from one end empties it rather than getting stuck on the
    /// last one, so a selection can always be undone by the same gesture that built it.
    /// </summary>
    [Fact]
    public void Toggle_RepeatedlyFromTheEnd_EventuallyClears()
    {
        var range = Range(2, 5);

        for (var i = 5; i > 2; i--)
        {
            range = QuickDictionaryAdd.Toggle(range, i);
        }

        Assert.Equal(Range(2, 2), range);
        Assert.True(QuickDictionaryAdd.Toggle(range, 2).IsEmpty);
    }

    [Fact]
    public void Toggle_NegativeIndex_LeavesSelectionAlone()
    {
        var result = QuickDictionaryAdd.Toggle(Range(4, 7), -1);

        Assert.Equal(Range(4, 7), result);
    }

    [Fact]
    public void Toggle_FirstWordInTranscript_ExtendsLeftWithoutRunningPastTheStart()
    {
        var result = QuickDictionaryAdd.Toggle(Range(1, 3), 0);

        Assert.Equal(Range(0, 3), result);
    }
}
