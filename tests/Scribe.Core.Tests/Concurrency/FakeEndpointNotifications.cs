using System.Collections.Concurrent;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests.Concurrency;

/// <summary>
/// Registrations as NAudio's notification client makes them, numbered, with every register, unregister and enumerator
/// release recorded in order, and a count of any call made from inside a notification callback.
/// </summary>
internal sealed class FakeEndpointNotifications
{
    private readonly ConcurrentQueue<string> _events = new();
    private Registration? _current;
    private Registration? _last;
    private int _registrations;
    private int _registerAttempts;
    private int _insideCallback;
    private int _callsInsideCallback;

    public IReadOnlyList<string> Events => [.. _events];

    public int CallsInsideCallback => Volatile.Read(ref _callsInsideCallback);

    public int RegisterAttempts => Volatile.Read(ref _registerAttempts);

    public Func<Exception>? ProbeFailure { get; set; }

    public Func<Exception>? RegisterFailure { get; set; }

    public IEndpointNotifications Register(Action<EndpointChange> notify)
    {
        NoteCall();
        Interlocked.Increment(ref _registerAttempts);
        if (RegisterFailure?.Invoke() is { } failure)
        {
            throw failure;
        }

        var registration = new Registration(this, Interlocked.Increment(ref _registrations), notify);
        _events.Enqueue($"register #{registration.Number}");
        _current = registration;
        _last = registration;
        return registration;
    }

    /// <summary>Delivers a notification as the audio worker thread does, to the live registration.</summary>
    public void Raise(EndpointChange change) => Deliver(_current, change);

    /// <summary>Delivers one to the last registration made, even after it was unregistered, as a late one can be.</summary>
    public void RaiseOnLastRegistration(EndpointChange change) => Deliver(_last, change);

    private void Deliver(Registration? registration, EndpointChange change)
    {
        if (registration is null)
        {
            return;
        }

        Interlocked.Increment(ref _insideCallback);
        try
        {
            registration.Notify(change);
        }
        finally
        {
            Interlocked.Decrement(ref _insideCallback);
        }
    }

    private void NoteCall()
    {
        if (Volatile.Read(ref _insideCallback) > 0)
        {
            Interlocked.Increment(ref _callsInsideCallback);
        }
    }

    private sealed class Registration(FakeEndpointNotifications owner, int number, Action<EndpointChange> notify)
        : IEndpointNotifications
    {
        public int Number { get; } = number;

        public Action<EndpointChange> Notify { get; } = notify;

        public void Probe()
        {
            owner.NoteCall();
            if (owner.ProbeFailure?.Invoke() is { } failure)
            {
                throw failure;
            }
        }

        public void Dispose()
        {
            owner.NoteCall();
            owner._events.Enqueue($"unregister #{Number}");
            owner._events.Enqueue($"release #{Number}");
            if (ReferenceEquals(owner._current, this))
            {
                owner._current = null;
            }
        }
    }
}
