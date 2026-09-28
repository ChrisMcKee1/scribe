using System.Collections.Specialized;
using System.ComponentModel;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

/// <summary>
/// IncrementalWordPackRows rebuilds a word pack's term rows with one Reset. The collection must then hold exactly the new
/// rows, say so once, and otherwise behave as <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> does.
/// </summary>
public sealed class ReplaceableObservableCollectionTests
{
    [Fact]
    public void Replacing_everything_raises_one_reset_and_one_count_and_indexer_change()
    {
        var collection = new ReplaceableObservableCollection<string> { "a", "b", "c" };
        var changes = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, e) =>
        {
            changes.Add(e.Action);
            Assert.Null(e.NewItems);
            Assert.Null(e.OldItems);
        };
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.ReplaceAll(["x", "y"]);

        Assert.Equal(["x", "y"], collection);
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(["Count", "Item[]"], properties);
    }

    [Fact]
    public void Replacing_with_nothing_leaves_it_empty_with_one_reset()
    {
        var collection = new ReplaceableObservableCollection<int> { 1, 2 };
        var resets = 0;
        collection.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        collection.ReplaceAll([]);

        Assert.Empty(collection);
        Assert.Equal(1, resets);
    }

    [Fact]
    public void A_handler_that_changes_the_collection_while_it_is_being_told_is_refused_as_before()
    {
        var collection = new ReplaceableObservableCollection<int>();
        collection.CollectionChanged += (_, _) => { };
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                Assert.Throws<InvalidOperationException>(() => collection.ReplaceAll([9]));
                Assert.Throws<InvalidOperationException>(() => collection.Add(9));
            }
        };

        collection.ReplaceAll([1, 2, 3]);

        Assert.Equal([1, 2, 3], collection);
    }

    [Fact]
    public void Everything_else_is_an_ordinary_observable_collection()
    {
        var collection = new ReplaceableObservableCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.Add(1);
        collection.Add(2);
        collection.Remove(1);
        collection.Clear();

        Assert.Equal(
            [NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Reset],
            actions);
        Assert.Throws<ArgumentNullException>(() => collection.ReplaceAll(null!));
    }
}
