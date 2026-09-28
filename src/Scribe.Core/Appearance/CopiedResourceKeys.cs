namespace Scribe.Core.Appearance;

/// <summary>
/// Tracks the resource keys an element copied from the application's own dictionary, so that a key the application has
/// since removed is removed from the copy too. WPF-UI 4.3.0's <c>ApplicationThemeManager.Apply(element)</c>, which the tray
/// menu relies on, copies every application-level entry into the element's resources and removes none: an override
/// Scribe took back (a contrast repair once the contrast theme ends, an accent correction the next plan drops) stayed in
/// the copy and outranked the theme.
/// </summary>
public static class CopiedResourceKeys
{
    /// <summary>
    /// Removes, through <paramref name="removeLocal"/>, each key in <paramref name="copied"/> that is not among the
    /// application's primary keys now, then records those keys as what the next copy holds. Pass the dictionary's own
    /// keys (<c>ResourceDictionary.Keys</c>), never a membership test that searches its merged dictionaries: the theme
    /// defines every key the application overrides, so such a test would keep every stale copy.
    /// </summary>
    /// <returns>How many copies were removed.</returns>
    public static int Reconcile(ISet<object> copied, IEnumerable<object> currentPrimaryKeys, Action<object> removeLocal)
    {
        ArgumentNullException.ThrowIfNull(copied);
        ArgumentNullException.ThrowIfNull(currentPrimaryKeys);
        ArgumentNullException.ThrowIfNull(removeLocal);

        var current = currentPrimaryKeys.ToHashSet();
        var removed = 0;
        foreach (var key in copied)
        {
            if (!current.Contains(key))
            {
                removeLocal(key);
                removed++;
            }
        }

        copied.Clear();
        copied.UnionWith(current);
        return removed;
    }
}
