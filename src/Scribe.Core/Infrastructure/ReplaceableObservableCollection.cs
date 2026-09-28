using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can also replace everything it holds at once, raising one Reset (and one
/// Count and indexer change) instead of a Reset for the clear and one Add per item (IncrementalWordPackRows: a word pack's
/// term rows are rebuilt on every selection, search and edit, and a DataGrid bound to them handled each Add on its own).
/// Everything else behaves exactly as <see cref="ObservableCollection{T}"/> does.
/// </summary>
public class ReplaceableObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs Reset = new(NotifyCollectionChangedAction.Reset);

    /// <summary>Holds exactly <paramref name="items"/>, in order, and says so with one Reset.</summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }

        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(Reset);
    }
}
