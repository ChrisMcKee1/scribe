namespace Scribe.Core.Hotkeys;

/// <summary>
/// A multi-producer, single-consumer hand-off in which the consumer never waits for a producer.
///
/// Producers push with a compare-and-swap loop; the consumer detaches everything at once with a
/// single exchange and walks it oldest first. A producer preempted mid-push has published nothing
/// yet, so it can only delay its own item, never the consumer. The hotkey hook uses this instead of
/// <c>ConcurrentQueue</c> or <c>BlockingCollection</c> because both can make the caller wait on a
/// monitor another thread holds (the queue's cross-segment lock on its slow path, the collection's
/// <c>SemaphoreSlim</c> on every add), and the hook callback runs against a hard OS deadline.
/// </summary>
internal sealed class LockFreeInbox<T>
{
    private Node? _head;

    public bool IsEmpty => Volatile.Read(ref _head) is null;

    /// <summary>Any thread. Allocates one node and never blocks.</summary>
    public void Push(T item)
    {
        var node = new Node(item);
        var head = Volatile.Read(ref _head);
        while (true)
        {
            node.Next = head;
            var observed = Interlocked.CompareExchange(ref _head, node, head);
            if (ReferenceEquals(observed, head))
            {
                return;
            }

            head = observed;
        }
    }

    /// <summary>
    /// Consumer only. Detaches every item pushed so far and returns them oldest first. Items pushed
    /// while the batch is being walked belong to the next call.
    /// </summary>
    public Batch TakeAll()
    {
        if (Volatile.Read(ref _head) is null)
        {
            return default;
        }

        // Pushes build a newest-first list. Once detached, the nodes belong to the consumer alone,
        // so reversing them in place restores arrival order without allocating.
        var newestFirst = Interlocked.Exchange(ref _head, null);
        Node? oldestFirst = null;
        while (newestFirst is not null)
        {
            var next = newestFirst.Next;
            newestFirst.Next = oldestFirst;
            oldestFirst = newestFirst;
            newestFirst = next;
        }

        return new Batch(oldestFirst);
    }

    internal sealed class Node(T item)
    {
        public readonly T Item = item;
        public Node? Next;
    }

    /// <summary>A detached run of items, enumerable without allocating.</summary>
    public readonly struct Batch
    {
        private readonly Node? _first;

        internal Batch(Node? first) => _first = first;

        public Enumerator GetEnumerator() => new(_first);
    }

    public struct Enumerator
    {
        private Node? _next;
        private T _current;

        internal Enumerator(Node? first)
        {
            _next = first;
            _current = default!;
        }

        public readonly T Current => _current;

        public bool MoveNext()
        {
            if (_next is null)
            {
                return false;
            }

            _current = _next.Item;
            _next = _next.Next;
            return true;
        }
    }
}
