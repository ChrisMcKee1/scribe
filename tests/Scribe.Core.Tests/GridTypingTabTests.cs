using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class GridTypingTabTests
{
    // The Dictionary grid in display order: Spoken, Replacement, Library, Whole word, Enabled, and the remove button.
    private static readonly bool[] DictionaryColumns = [true, true, false, false, false, false];

    [Fact]
    public void Tab_from_the_spoken_form_moves_to_its_replacement()
    {
        Assert.Equal(1, GridTypingTab.Next(DictionaryColumns, current: 0, backwards: false));
    }

    [Fact]
    public void Shift_tab_from_the_replacement_moves_back_to_the_spoken_form()
    {
        Assert.Equal(0, GridTypingTab.Next(DictionaryColumns, current: 1, backwards: true));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void Past_the_last_typed_cell_in_the_direction_of_the_key_it_leaves_the_grid(int current, bool backwards)
    {
        Assert.Null(GridTypingTab.Next(DictionaryColumns, current, backwards));
    }

    [Theory]
    [InlineData(0, false, 3)]
    [InlineData(3, true, 0)]
    public void Columns_that_take_no_typing_are_skipped(int current, bool backwards, int expected)
    {
        bool[] columns = [true, false, false, true, false];

        Assert.Equal(expected, GridTypingTab.Next(columns, current, backwards));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void A_cell_outside_the_row_leaves_the_grid(int current)
    {
        Assert.Null(GridTypingTab.Next(DictionaryColumns, current, backwards: false));
        Assert.Null(GridTypingTab.Next(DictionaryColumns, current, backwards: true));
    }

    [Fact]
    public void A_grid_with_no_columns_leaves_the_grid()
    {
        Assert.Null(GridTypingTab.Next([], current: 0, backwards: false));
    }
}
