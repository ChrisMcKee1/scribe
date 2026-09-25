namespace Scribe.Core.Settings;

public enum ChipKey
{
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    Space,
    Enter,
    Other,
}

public enum ChipKeyboardOutcome
{
    StayInWords,
    MoveToWrites,
}

public sealed record ChipKeyboardState(int FocusIndex, int AnchorIndex, QuickDictionaryAdd.WordRange Selection, IReadOnlyList<int>? RowByIndex = null);

public sealed record ChipKeyboardResult(ChipKeyboardState State, ChipKeyboardOutcome Outcome);

public static class ChipKeyboard
{
    public static ChipKeyboardResult Apply(ChipKeyboardState state, ChipKey key, bool shift, int wordCount)
    {
        if (wordCount <= 0) return new ChipKeyboardResult(new ChipKeyboardState(-1, -1, QuickDictionaryAdd.WordRange.None, state.RowByIndex), ChipKeyboardOutcome.StayInWords);
        var focus = state.FocusIndex < 0 ? FirstFocus(state.Selection) : Math.Clamp(state.FocusIndex, 0, wordCount - 1);
        var anchor = state.AnchorIndex < 0 ? focus : Math.Clamp(state.AnchorIndex, 0, wordCount - 1);
        var selection = state.Selection;
        switch (key)
        {
            case ChipKey.Left:
                focus = Math.Max(0, focus - 1);
                break;
            case ChipKey.Right:
                focus = Math.Min(wordCount - 1, focus + 1);
                break;
            case ChipKey.Home:
                focus = 0;
                break;
            case ChipKey.End:
                focus = wordCount - 1;
                break;
            case ChipKey.Up:
                focus = MoveVertical(focus, -1, state.RowByIndex);
                break;
            case ChipKey.Down:
                focus = MoveVertical(focus, 1, state.RowByIndex);
                break;
            case ChipKey.Space:
                var previous = selection;
                selection = QuickDictionaryAdd.Toggle(selection, focus);
                anchor = AnchorAfterToggle(previous, selection, focus);
                return new ChipKeyboardResult(new ChipKeyboardState(focus, anchor, selection, state.RowByIndex), ChipKeyboardOutcome.StayInWords);
            case ChipKey.Enter:
                return new ChipKeyboardResult(new ChipKeyboardState(focus, anchor, selection, state.RowByIndex), selection.IsEmpty ? ChipKeyboardOutcome.StayInWords : ChipKeyboardOutcome.MoveToWrites);
            default:
                return new ChipKeyboardResult(new ChipKeyboardState(focus, anchor, selection, state.RowByIndex), ChipKeyboardOutcome.StayInWords);
        }

        if (shift)
        {
            selection = new QuickDictionaryAdd.WordRange(Math.Min(anchor, focus), Math.Max(anchor, focus));
        }
        else
        {
            anchor = focus;
        }

        return new ChipKeyboardResult(new ChipKeyboardState(focus, anchor, selection, state.RowByIndex), ChipKeyboardOutcome.StayInWords);
    }

    private static int AnchorAfterToggle(QuickDictionaryAdd.WordRange previous, QuickDictionaryAdd.WordRange next, int focus)
    {
        if (next.IsEmpty)
        {
            return focus;
        }

        if (previous.IsEmpty)
        {
            return focus;
        }

        return next.First == previous.First ? next.First : next.Last;
    }

    private static int FirstFocus(QuickDictionaryAdd.WordRange selection) => selection.IsEmpty ? 0 : selection.First;

    private static int MoveVertical(int focus, int direction, IReadOnlyList<int>? rows)
    {
        if (rows is null || focus < 0 || focus >= rows.Count) return focus;
        var currentRow = rows[focus];
        var targetRow = currentRow + direction;
        var candidates = rows.Select((row, index) => (row, index)).Where(x => x.row == targetRow).Select(x => x.index).ToList();
        if (candidates.Count == 0) return focus;
        return candidates.OrderBy(index => Math.Abs(index - focus)).ThenBy(index => index).First();
    }
}
