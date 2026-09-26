using System.ClientModel.Primitives;
using Scribe.Core.Libraries;

namespace Scribe.Core.Cleanup;

/// <summary>What a handed-over or held-back cleanup request was; logged by name, never with any content.</summary>
internal enum CleanupRequestKind
{
    /// <summary>A dictation's cleanup: each chunk, each retry, on either Azure surface.</summary>
    Dictation,

    /// <summary>The readiness probe, which carries no vocabulary.</summary>
    Probe,

    /// <summary>A one-off completion: the dictionary suggester or the usage insight.</summary>
    Completion,
}

/// <summary>Why a cleanup request was not handed over; logged by name, never with any content.</summary>
internal enum HandOffRefusal
{
    /// <summary>It was handed over.</summary>
    None,

    /// <summary>It reached a gate with no admission, which only a defect can cause.</summary>
    NoAdmission,

    /// <summary>The published library scope no longer covers the one its vocabulary was admitted with.</summary>
    LibraryScope,

    /// <summary>
    /// A request bound to a recipient found cleanup no longer ready for it: switched off, setting up another
    /// configuration, unavailable, or closing.
    /// </summary>
    NotReady,

    /// <summary>A request bound to a recipient found the service serving another configuration.</summary>
    RecipientChanged,
}

/// <summary>
/// The admission one cleanup request runs under: the library scope its vocabulary was admitted with, and the source
/// that decides, at the moment the request would leave, whether that scope still holds
/// (<see cref="ILibraryVocabularySource.TryHandOff"/>, contract 2.10). A one-off completion is also bound to the
/// recipient its caller captured (release 0.4.4's recipient rule), which is judged at the same moment.
/// </summary>
/// <remarks>
/// Carried ambiently, from the agent run <see cref="TextCleanupService"/> makes down to the last in-process step before
/// the request leaves (<see cref="VocabularyHandOffHandler"/> for an HTTP provider, <see cref="GitHubCopilotCleanupAgent"/>
/// for the Copilot runtime), because the Agent Framework and OpenAI SDK layers in between have no way to carry it. The
/// service sets it around every agent run it makes, and nothing else sets it, so a request that reaches either gate
/// without one was made by something the service does not know, and is refused.
/// </remarks>
internal sealed class CleanupAdmission
{
    private static readonly AsyncLocal<CleanupAdmission?> Ambient = new();

    private readonly ILibraryVocabularySource? _source;
    private readonly Func<Action, HandOffRefusal>? _whileServing;
    private int _handedOver;
    private int _refused;

    /// <param name="kind">What the request is, for the log.</param>
    /// <param name="scope">The library scope its vocabulary was admitted with.</param>
    /// <param name="source">The admission point; null only where no library vocabulary can reach the service.</param>
    /// <param name="whileServing">
    /// For a request bound to a recipient: runs the send it is given only while the service still serves that recipient
    /// and is ready, in one step with the check, and returns why not otherwise. Null for a request bound to its library
    /// scope alone.
    /// </param>
    internal CleanupAdmission(
        CleanupRequestKind kind,
        AiVocabularyScope scope,
        ILibraryVocabularySource? source,
        Func<Action, HandOffRefusal>? whileServing = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Kind = kind;
        Scope = scope;
        _source = source;
        _whileServing = whileServing;
    }

    /// <summary>The admission of the calling flow, or null outside every request the service makes.</summary>
    internal static CleanupAdmission? Current => Ambient.Value;

    internal CleanupRequestKind Kind { get; }

    internal AiVocabularyScope Scope { get; }

    /// <summary>
    /// How many requests this admission handed over, and how many it held back: one per HTTP attempt, and for the Copilot
    /// runtime one for a session's creation and one for its send.
    /// </summary>
    internal int HandedOver => Volatile.Read(ref _handedOver);

    internal int Refused => Volatile.Read(ref _refused);

    /// <summary>Makes this the ambient admission of the calling flow until the result is disposed.</summary>
    internal Entered Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Entered(previous);
    }

    /// <summary>
    /// Runs <paramref name="send"/>, which starts the request and returns without waiting for its response, when the
    /// published scope still covers <see cref="Scope"/>, under the source's permission gate; for a request bound to a
    /// recipient, only when the service still serves it and is ready as well, checked under the service's own gate inside
    /// the permission gate, so one check and the start of the send are a single step against a revocation and a
    /// reconfiguration alike. Nothing awaits under either gate. With no source (a service built without the library
    /// vocabulary, as tests and tools build it) there is no library permission to check.
    /// </summary>
    internal bool TryHandOff(Action send) => TryHandOff(send, out _);

    /// <inheritdoc cref="TryHandOff(Action)"/>
    /// <param name="send">Starts the request and returns without waiting for its response.</param>
    /// <param name="refusal">Why the request was not handed over, or <see cref="HandOffRefusal.None"/> when it was.</param>
    internal bool TryHandOff(Action send, out HandOffRefusal refusal)
    {
        var served = HandOffRefusal.None;
        void SendWhileServed()
        {
            if (_whileServing is null)
            {
                send();
            }
            else
            {
                served = _whileServing(send);
            }
        }

        var ran = _source is null ? RunWithoutSource(SendWhileServed) : _source.TryHandOff(Scope, SendWhileServed);
        refusal = ran ? served : HandOffRefusal.LibraryScope;
        var handedOver = refusal == HandOffRefusal.None;
        Interlocked.Increment(ref handedOver ? ref _handedOver : ref _refused);
        return handedOver;
    }

    private static bool RunWithoutSource(Action send)
    {
        send();
        return true;
    }

    internal readonly struct Entered(CleanupAdmission? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// Thrown in place of a request the admission point did not hand over, so the request never leaves: the library
/// vocabulary it was admitted with is no longer permitted, the recipient it was bound to is no longer served or ready, or
/// it reached a gate with no admission at all. Its message is fixed text; it never names a library, a term or an endpoint.
/// </summary>
internal sealed class VocabularyHandOffRefusedException : InvalidOperationException
{
    internal VocabularyHandOffRefusedException(HandOffRefusal reason)
        : base(MessageFor(reason))
    {
        Reason = reason;
    }

    /// <summary>Why the request was not handed over; never <see cref="HandOffRefusal.None"/>.</summary>
    internal HandOffRefusal Reason { get; }

    /// <summary>False when the request reached the gate with no admission, which only a defect can cause.</summary>
    internal bool Admitted => Reason != HandOffRefusal.NoAdmission;

    /// <summary>The refusal in <paramref name="exception"/> or any exception it wraps, or null.</summary>
    internal static VocabularyHandOffRefusedException? Find(Exception? exception) => Find(exception, depth: 0);

    private static string MessageFor(HandOffRefusal reason) => reason switch
    {
        HandOffRefusal.NoAdmission => "The request was not handed over: it carried no admission.",
        HandOffRefusal.LibraryScope =>
            "The request was not handed over: the library vocabulary it was admitted with is no longer permitted.",
        HandOffRefusal.NotReady => "The request was not handed over: AI cleanup is no longer ready for it.",
        HandOffRefusal.RecipientChanged => "The request was not handed over: AI cleanup now serves another configuration.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "A request that was handed over was not refused."),
    };

    // Depth-bounded: an exception chain that loops must not hang the failure path.
    private static VocabularyHandOffRefusedException? Find(Exception? exception, int depth)
    {
        if (exception is null || depth > 16)
        {
            return null;
        }

        if (exception is VocabularyHandOffRefusedException refused)
        {
            return refused;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (Find(inner, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return Find(exception.InnerException, depth + 1);
    }
}

/// <summary>
/// The admission point for an HTTP provider (contract 2.10): a <see cref="DelegatingHandler"/> placed just before the
/// transport handler, so every attempt the OpenAI client makes (the first, each of its own retries, either Azure
/// surface) is handed over only through <see cref="CleanupAdmission.TryHandOff"/>, whose hand-off starts the inner
/// handler's send and returns without waiting for the response.
/// </summary>
internal sealed class VocabularyHandOffHandler : DelegatingHandler
{
    private static readonly Lazy<HttpClientPipelineTransport> Shared = new(() =>
        CreateTransport(new HttpClientHandler { AllowAutoRedirect = false }));

    internal VocabularyHandOffHandler(HttpMessageHandler network)
        : base(network)
    {
    }

    /// <summary>
    /// The transport every cleanup client uses: System.ClientModel's own default (an <see cref="HttpClientHandler"/> that
    /// follows no redirect, no client timeout since the pipeline times each attempt), with the hand-off in front of it.
    /// One for the process, as the default is, so connections are pooled across reconfigurations as before.
    /// </summary>
    internal static HttpClientPipelineTransport SharedTransport => Shared.Value;

    /// <summary>A transport with the hand-off in front of <paramref name="network"/>; tests put a fake network there.</summary>
    internal static HttpClientPipelineTransport CreateTransport(HttpMessageHandler network) =>
        new(new HttpClient(new VocabularyHandOffHandler(network)) { Timeout = Timeout.InfiniteTimeSpan });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (CleanupAdmission.Current is not { } admission)
        {
            return Task.FromException<HttpResponseMessage>(new VocabularyHandOffRefusedException(HandOffRefusal.NoAdmission));
        }

        Task<HttpResponseMessage>? sending = null;
        if (!admission.TryHandOff(() => sending = StartSend(request, cancellationToken), out var refusal))
        {
            return Task.FromException<HttpResponseMessage>(new VocabularyHandOffRefusedException(refusal));
        }

        return sending!;
    }

    // The pipeline's synchronous path, which Scribe never takes: the same hand-off, and the wait outside the gate.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    // Never throws inside the permission gate: a send that fails to start fails its own task instead.
    private Task<HttpResponseMessage> StartSend(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
