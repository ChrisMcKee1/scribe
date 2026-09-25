using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class ChipKeyboardTests
{
    [Fact]
    public void Arrows_move_focus_only()
    {
        var state = State(focus: 1, anchor: 1, selection: new QuickDictionaryAdd.WordRange(1, 1));
        var result = ChipKeyboard.Apply(state, ChipKey.Right, shift: false, wordCount: 4);

        Assert.Equal(2, result.State.FocusIndex);
        Assert.Equal(new QuickDictionaryAdd.WordRange(1, 1), result.State.Selection);
    }

    [Fact]
    public void Up_and_down_use_supplied_row_targets()
    {
        var state = State(focus: 1, rows: [0, 0, 1, 1]);

        Assert.Equal(2, ChipKeyboard.Apply(state, ChipKey.Down, false, 4).State.FocusIndex);
        Assert.Equal(1, ChipKeyboard.Apply(State(focus: 2, rows: [0, 0, 1, 1]), ChipKey.Up, false, 4).State.FocusIndex);
    }

    [Fact]
    public void Home_and_end_focus_edges()
    {
        Assert.Equal(0, ChipKeyboard.Apply(State(focus: 2), ChipKey.Home, false, 4).State.FocusIndex);
        Assert.Equal(3, ChipKeyboard.Apply(State(focus: 1), ChipKey.End, false, 4).State.FocusIndex);
    }

    [Fact]
    public void Space_uses_the_click_rule()
    {
        var result = ChipKeyboard.Apply(State(focus: 1, selection: new QuickDictionaryAdd.WordRange(0, 0)), ChipKey.Space, false, 3);

        Assert.Equal(new QuickDictionaryAdd.WordRange(0, 1), result.State.Selection);
        Assert.Equal(1, result.State.FocusIndex);
    }

    [Fact]
    public void Shift_arrow_extends_from_the_anchor()
    {
        var result = ChipKeyboard.Apply(State(focus: 1, anchor: 1, selection: new QuickDictionaryAdd.WordRange(1, 1)), ChipKey.Right, true, 4);

        Assert.Equal(new QuickDictionaryAdd.WordRange(1, 2), result.State.Selection);
    }

    [Fact]
    public void Shift_arrow_with_nothing_selected_anchors_from_focus()
    {
        var result = ChipKeyboard.Apply(State(focus: 1, anchor: -1), ChipKey.Right, true, 4);

        Assert.Equal(new QuickDictionaryAdd.WordRange(1, 2), result.State.Selection);
    }

    [Fact]
    public void Shift_home_and_end_select_to_edges()
    {
        Assert.Equal(new QuickDictionaryAdd.WordRange(0, 2), ChipKeyboard.Apply(State(focus: 2, anchor: 2), ChipKey.Home, true, 4).State.Selection);
        Assert.Equal(new QuickDictionaryAdd.WordRange(1, 3), ChipKeyboard.Apply(State(focus: 1, anchor: 1), ChipKey.End, true, 4).State.Selection);
    }

    [Fact]
    public void Enter_moves_to_writes_only_with_selection_and_never_saves()
    {
        Assert.Equal(ChipKeyboardOutcome.StayInWords, ChipKeyboard.Apply(State(selection: QuickDictionaryAdd.WordRange.None), ChipKey.Enter, false, 3).Outcome);
        Assert.Equal(ChipKeyboardOutcome.MoveToWrites, ChipKeyboard.Apply(State(selection: new QuickDictionaryAdd.WordRange(0, 0)), ChipKey.Enter, false, 3).Outcome);
    }

    [Fact]
    public void Empty_dictation_is_safe()
    {
        var result = ChipKeyboard.Apply(State(focus: 2), ChipKey.Right, false, 0);

        Assert.Equal(-1, result.State.FocusIndex);
        Assert.True(result.State.Selection.IsEmpty);
    }

    private static ChipKeyboardState State(int focus = 0, int anchor = 0, QuickDictionaryAdd.WordRange? selection = null, IReadOnlyList<int>? rows = null) => new(focus, anchor, selection ?? QuickDictionaryAdd.WordRange.None, rows);
}
