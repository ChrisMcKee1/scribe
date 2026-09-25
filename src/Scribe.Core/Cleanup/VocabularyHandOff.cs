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

/// <summary>
/// The admission one cleanup request runs under: the library scope its vocabulary was admitted with, and the source
/// that decides, at the moment the request would leave, whether that scope still holds
/// (<see cref="ILibraryVocabularySource.TryHandOff"/>, contract 2.10).
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
    private int _handedOver;
    private int _refused;

    internal CleanupAdmission(CleanupRequestKind kind, AiVocabularyScope scope, ILibraryVocabularySource? source)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Kind = kind;
        Scope = scope;
        _source = source;
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
    /// published scope still covers <see cref="Scope"/>, under the source's permission gate. With no source (a service
    /// built without the library vocabulary, as tests and tools build it) there is no permission to check, and it runs.
    /// </summary>
    internal bool TryHandOff(Action send)
    {
        bool handedOver;
        if (_source is null)
        {
            send();
            handedOver = true;
        }
        else
        {
            handedOver = _source.TryHandOff(Scope, send);
        }

        Interlocked.Increment(ref handedOver ? ref _handedOver : ref _refused);
        return handedOver;
    }

    internal readonly struct Entered(CleanupAdmission? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// Thrown in place of a request the admission point did not hand over, so the request never leaves: the library
/// vocabulary it was admitted with is no longer permitted, or it reached a gate with no admission at all. Its message
/// is fixed text; it never names a library, a term or an endpoint.
/// </summary>
internal sealed class VocabularyHandOffRefusedException : InvalidOperationException
{
    internal VocabularyHandOffRefusedException(bool admitted)
        : base(admitted
            ? "The request was not handed over: the library vocabulary it was admitted with is no longer permitted."
            : "The request was not handed over: it carried no admission.")
    {
        Admitted = admitted;
    }

    /// <summary>False when the request reached the gate with no admission, which only a defect can cause.</summary>
    internal bool Admitted { get; }

    /// <summary>The refusal in <paramref name="exception"/> or any exception it wraps, or null.</summary>
    internal static VocabularyHandOffRefusedException? Find(Exception? exception) => Find(exception, depth: 0);

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
            return Task.FromException<HttpResponseMessage>(new VocabularyHandOffRefusedException(admitted: false));
        }

        Task<HttpResponseMessage>? sending = null;
        if (!admission.TryHandOff(() => sending = StartSend(request, cancellationToken)))
        {
            return Task.FromException<HttpResponseMessage>(new VocabularyHandOffRefusedException(admitted: true));
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
