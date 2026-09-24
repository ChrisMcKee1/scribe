namespace Scribe.Core.Settings;

/// <summary>
/// Where Tab goes while the user is typing in a cell of an editable settings grid such as the Dictionary: to the
/// nearest cell of the same row that takes typing, in the direction of the key, so an entry is filled in left to
/// right. With none left in that direction the key leaves the grid, as Tab does from any cell that is not being
/// typed into, because each grid is a single Tab stop whose cells the arrow keys move between.
/// </summary>
public static class GridTypingTab
{
    /// <param name="typeable">For each column in display order, whether its cells take typing.</param>
    /// <param name="current">The display index of the cell being typed into.</param>
    /// <param name="backwards">Shift+Tab rather than Tab.</param>
    /// <returns>The display index of the cell to type into next, or <see langword="null"/> to leave the grid.</returns>
    public static int? Next(IReadOnlyList<bool> typeable, int current, bool backwards)
    {
        ArgumentNullException.ThrowIfNull(typeable);
        if (current < 0 || current >= typeable.Count)
        {
            return null;
        }

        var step = backwards ? -1 : 1;
        for (var index = current + step; index >= 0 && index < typeable.Count; index += step)
        {
            if (typeable[index])
            {
                return index;
            }
        }

        return null;
    }
}
