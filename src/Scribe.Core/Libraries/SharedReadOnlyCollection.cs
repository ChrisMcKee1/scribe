using System.Collections;
using System.Collections.ObjectModel;

namespace Scribe.Core.Libraries;

/// <summary>
/// A <see cref="ReadOnlyCollection{T}"/> whose non-generic <see cref="ICollection.SyncRoot"/> is the wrapper itself, for
/// the collections a shared object hands out. A plain one returns the wrapped list's SyncRoot, which for a
/// <see cref="List{T}"/> is the list and for an array is the array: a caller could reach the storage behind it, without
/// reflection, and change what every other caller reads. Everything else is the base class's, so the runtime type is
/// still a ReadOnlyCollection and a binding sees the same IList.
/// </summary>
/// <remarks>
/// Only a list or an array is wrapped, both of which implement <see cref="ICollection"/>, so CopyTo always has the
/// wrapped list's own copy to delegate to.
/// </remarks>
internal sealed class SharedReadOnlyCollection<T> : ReadOnlyCollection<T>, ICollection
{
    public SharedReadOnlyCollection(List<T> list)
        : base(list)
    {
    }

    public SharedReadOnlyCollection(T[] array)
        : base(array)
    {
    }

    int ICollection.Count => Count;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    void ICollection.CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);
}
