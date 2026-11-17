using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Transcription;

public sealed record MaiTranscribeOptions(
    bool Enabled,
    string? MaiEndpointOverride,
    string? FoundryEndpoint,
    string? ApiKey,
    AzureAuthMode AuthMode = AzureAuthMode.AzureCli,
    string? TenantId = null,
    string? SubscriptionId = null,
    string? ClientId = null,
    string? ClientSecret = null,
    MaiTranscribeStyle Style = MaiTranscribeStyle.Clean,
    IReadOnlyList<string>? PhraseList = null,
    MaiPhraseAdmission? PhraseAdmission = null)
{
    public static MaiTranscribeOptions Disabled { get; } = new(false, null, null, null);

    /// <summary>
    /// Whether <paramref name="settings"/> choose MAI-Transcribe-2 and this installation may use it (a Store package keeps
    /// it off unless its flag is set). This alone decides whether a dictation's audio can leave this PC, so it is asked
    /// before anything else cloud speech needs is read.
    /// </summary>
    public static bool IsSelected(AppSettings settings, bool isPackaged, PerfFlags? perfFlags = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return string.Equals(
                settings.TranscriptionModelId?.Trim(),
                TranscriptionModelCatalog.MaiTranscribe2Id,
                StringComparison.OrdinalIgnoreCase) &&
            InstallerFeatureGate.CanUseMaiTranscribe(isPackaged, perfFlags);
    }

    /// <summary>
    /// The options one dictation's cloud speech runs with, decided from the admission its recording was admitted with: its
    /// settings choose whether the audio may leave this PC, and its vocabulary generation, with the library scope that
    /// permitted it, supplies the words that may go with it. Nothing is read from the stored settings or the dictionary,
    /// and the library source (the admission point each send is judged by) is looked up only once cloud speech is chosen
    /// and words are to be sent, so a dictation that stays on this PC reads nothing at all. Library words are carried only
    /// when a source can judge them at the send; with none, they are left out.
    /// </summary>
    internal static MaiTranscribeOptions FromAdmission(
        TranscriptionAdmission admission,
        bool isPackaged,
        PerfFlags? perfFlags,
        Func<ILibraryVocabularySource?> librarySource)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(librarySource);

        var settings = admission.Settings;
        if (!IsSelected(settings, isPackaged, perfFlags))
        {
            return Disabled;
        }

        if (!settings.ApplyPostProcessing)
        {
            return FromSettings(settings, isPackaged, perfFlags);
        }

        var source = librarySource();
        var libraries = source is null ? null : admission.Vocabulary.Libraries;
        return FromSettings(settings, isPackaged, perfFlags, admission.Vocabulary.Dictionary, libraries, source);
    }

    /// <summary>
    /// The options for <paramref name="settings"/>, with the personal words and, when a <paramref name="librarySource"/>
    /// can judge them, the AI-permitted word pack words of <paramref name="libraries"/>. A word pack's words are never
    /// carried without that source: with <paramref name="libraries"/> and no source, the library words are left out and the
    /// personal words alone are kept, because nothing could stop them at a send if their permission were withdrawn.
    /// </summary>
    public static MaiTranscribeOptions FromSettings(
        AppSettings settings,
        bool isPackaged,
        PerfFlags? perfFlags = null,
        IEnumerable<DictionaryEntry>? personalDictionary = null,
        LibraryVocabulary? libraries = null,
        ILibraryVocabularySource? librarySource = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsSelected(settings, isPackaged, perfFlags))
        {
            return Disabled;
        }

        var tenantId = settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal
            ? settings.AiCleanupAzureTenantId
            : AzureSubscriptionSelection.ResolveTenantId(
                settings.AiCleanupAzureSubscriptionId,
                settings.AiCleanupAzureSubscriptionTenantId,
                settings.AiCleanupAzureTenantId);

        // The library words came from this scope, as the recording was admitted with it, so every send is judged against
        // it. Library words and their admission are one decision: where there is no source to judge them, there are none.
        var judgedLibraries = settings.ApplyPostProcessing && librarySource is not null ? libraries : null;
        MaiPhraseAdmission? phraseAdmission = judgedLibraries is not null && librarySource is not null
            ? new MaiPhraseAdmission(judgedLibraries.AiScope, librarySource)
            : null;

        var phrases = settings.ApplyPostProcessing
            ? MaiTranscribePhraseList.Build(personalDictionary, judgedLibraries?.AiEntries)
            : [];

        return new MaiTranscribeOptions(
            Enabled: true,
            MaiEndpointOverride: settings.MaiTranscribeEndpoint,
            FoundryEndpoint: settings.AiCleanupAzureEndpoint,
            ApiKey: settings.AiCleanupAzureApiKey,
            AuthMode: settings.AiCleanupAzureAuthMode,
            TenantId: tenantId,
            SubscriptionId: settings.AiCleanupAzureSubscriptionId,
            ClientId: settings.AiCleanupAzureClientId,
            ClientSecret: settings.AiCleanupAzureClientSecret,
            Style: settings.MaiTranscribeStyle,
            PhraseList: phrases,
            PhraseAdmission: phraseAdmission);
    }
}

/// <summary>
/// The library scope a phrase list was drawn from, and the admission point that judges it. The words are copied when the
/// dictation is admitted, but a word pack's AI permission can be withdrawn at any moment after that, while credentials
/// are acquired or between the first send and its retry. So the client hands every send over only through
/// <see cref="ILibraryVocabularySource.TryHandOff"/> with this scope, as the last step before the request leaves, and a
/// send that is refused is not made: the dictation is decoded on this PC instead.
/// </summary>
public sealed record MaiPhraseAdmission(AiVocabularyScope Scope, ILibraryVocabularySource Source);

public sealed record MaiTranscribeAttemptResult(
    bool Succeeded,
    string Text,
    TimeSpan Elapsed,
    string DiagnosticReason)
{
    public static MaiTranscribeAttemptResult Success(string text, TimeSpan elapsed) =>
        new(true, text, elapsed, "ok");

    public static MaiTranscribeAttemptResult Fallback(string reason, TimeSpan elapsed = default) =>
        new(false, string.Empty, elapsed, reason);
}

public interface IMaiTranscribeClient
{
    Task<MaiTranscribeAttemptResult> TryTranscribeAsync(
        CapturedAudio audio,
        MaiTranscribeOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a Microsoft Foundry or Azure Speech resource address to the <c>MAI-Transcribe-2</c>
/// <c>/speechtotext/transcriptions:transcribe?api-version=2025-10-15</c> REST endpoint.
/// </summary>
public static class MaiTranscribeEndpointResolver
{
    public const string ApiVersion = "2025-10-15";
    public const string TranscribePath = "/speechtotext/transcriptions:transcribe";

    public static bool TryResolve(
        string? maiEndpointOverride,
        string? foundryEndpoint,
        out Uri? requestUri,
        out string? errorCode)
    {
        requestUri = null;
        var raw = !string.IsNullOrWhiteSpace(maiEndpointOverride)
            ? maiEndpointOverride.Trim()
            : foundryEndpoint?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            errorCode = "endpoint-unset";
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            errorCode = "endpoint-invalid";
            return false;
        }

        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            LocalAiServer.IsOnThisPc(raw);
        if (!isHttps && !isLoopbackHttp)
        {
            errorCode = "endpoint-scheme";
            return false;
        }

        var builder = new UriBuilder(parsed);
        if (builder.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = builder.Host[..^".openai.azure.com".Length];
            if (prefix.Length > 0)
            {
                builder.Host = prefix + ".cognitiveservices.azure.com";
            }
        }

        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith(TranscribePath, StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = TranscribePath;
        }

        var query = builder.Query.TrimStart('?');
        if (!query.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            builder.Query = string.IsNullOrEmpty(query)
                ? $"api-version={ApiVersion}"
                : $"{query}&api-version={ApiVersion}";
        }

        requestUri = builder.Uri;
        errorCode = null;
        return true;
    }
}

/// <summary>
/// Builds the <c>phraseList.phrases</c> keyword-biasing list for <c>MAI-Transcribe-2</c> from the user's
/// enabled dictionary entries and the entries of the AI-permitted word packs, enforcing the same single-line
/// 100-character vocabulary rule as AI cleanup. The library entries are those of the vocabulary the dictation was
/// admitted with (<see cref="LibraryVocabulary.AiEntries"/>). The list itself carries no permission: the
/// <see cref="MaiPhraseAdmission"/> next to it in the options is judged again at every send.
/// </summary>
public static class MaiTranscribePhraseList
{
    public const int MaxPhrases = 500;

    public static IReadOnlyList<string> Build(
        IEnumerable<DictionaryEntry>? personalEntries,
        IEnumerable<DictionaryEntry>? libraryEntries = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phrases = new List<string>();

        if (personalEntries is not null)
        {
            foreach (var entry in personalEntries)
            {
                if (!entry.Enabled || !CleanupPrompt.IsVocabularyReplacement(entry.Replacement))
                {
                    continue;
                }

                var candidate = entry.Replacement.Trim();
                if (candidate.Length > 0 && seen.Add(candidate))
                {
                    phrases.Add(candidate);
                    if (phrases.Count >= MaxPhrases)
                    {
                        return phrases;
                    }
                }
            }
        }

        if (libraryEntries is not null)
        {
            foreach (var entry in libraryEntries)
            {
                if (!CleanupPrompt.IsVocabularyReplacement(entry.Replacement))
                {
                    continue;
                }

                var candidate = entry.Replacement.Trim();
                if (candidate.Length > 0 && seen.Add(candidate))
                {
                    phrases.Add(candidate);
                    if (phrases.Count >= MaxPhrases)
                    {
                        break;
                    }
                }
            }
        }

        return phrases;
    }
}

/// <summary>
/// Encodes 16 kHz mono float samples into an in-memory 16-bit PCM RIFF/WAVE payload for <c>MAI-Transcribe-2</c>.
/// Audio is encoded in memory and never written to disk.
/// </summary>
public static class MaiWavEncoder
{
    private const int HeaderSize = 44;

    public static byte[] EncodePcm16Wav(ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var dataBytes = checked(samples.Length * sizeof(short));
        var buffer = new byte[checked(HeaderSize + dataBytes)];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span[0..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], HeaderSize - 8 + dataBytes);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], 1); // Mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..32], sampleRate * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], 16);
        "data"u8.CopyTo(span[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], dataBytes);

        var offset = HeaderSize;
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var pcm = (short)MathF.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset, sizeof(short)), pcm);
            offset += sizeof(short);
        }

        return buffer;
    }
}

/// <summary>
/// Calls <c>MAI-Transcribe-2</c> on Microsoft Foundry (<c>transcriptions:transcribe</c>) with
/// vocabulary biasing and clean/verbatim style. Any configuration or network failure returns a
/// shape-only fallback result so <see cref="TranscriptionService"/> can decode on this PC with Parakeet.
/// </summary>
/// <remarks>
/// Four rules keep the audio and the user's words where they were allowed to go, and keep a dictation from being lost to
/// the cloud. Every send is handed over only through the library admission point with the scope its phrases were admitted
/// with (<see cref="MaiPhraseAdmission"/>), as the last step before the request leaves, the retry included. The transport
/// follows no redirect, so the audio, the phrases and the key header can only reach the address that was configured. One
/// deadline bounds the whole attempt, credentials included, and the caller's cancellation reaches every step. And an answer
/// Scribe does not recognize is a failure to decode on this PC, never an empty dictation.
/// </remarks>
public sealed class MaiTranscribeClient : IMaiTranscribeClient, IDisposable
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    // The whole attempt's budget: acquiring credentials, every send (the retry without enhanced mode included) and reading
    // the answer. It was once the HTTP client's own timeout, which began only after the credential and restarted for the
    // retry, so a sign-in queued behind another Azure CLI request held the dictation for minutes before it started.
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(25);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    // The one place a credential is built (the seam below AcquireTokenAsync): the shared Azure credential factory unless a
    // test supplies its own, so the scope asked for and the cancellation handed down run as shipped over any credential.
    private readonly Func<AzureCredentialRequest, TokenCredential> _credentialFactory;
    private readonly ILogger<MaiTranscribeClient> _logger;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _timeProvider;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _fastTranscriptionFallbackHosts =
        new(StringComparer.OrdinalIgnoreCase);

    public MaiTranscribeClient(
        ILogger<MaiTranscribeClient> logger,
        PerfFlags? perfFlags = null,
        HttpMessageHandler? handler = null)
        : this(logger, perfFlags, handler, credentialFactory: null)
    {
    }

    internal MaiTranscribeClient(
        ILogger<MaiTranscribeClient> logger,
        PerfFlags? perfFlags,
        HttpMessageHandler? handler,
        Func<AzureCredentialRequest, TokenCredential>? credentialFactory,
        TimeSpan? deadline = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _deadline = deadline ?? DefaultDeadline;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = handler is null;

        // No timeout of the client's own: the attempt's one deadline (TryTranscribeAsync) bounds every step, so a second,
        // per-send limit can never disagree with it.
        _httpClient = handler is null
            ? new HttpClient(CreateNetworkHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _credentialFactory = credentialFactory ?? (request => AzureCredentialFactory.Create(request));
    }

    /// <summary>
    /// The transport this client sends through unless one is supplied. It follows no redirect: a configured address that
    /// answers with a redirect, to any other address, would otherwise have the whole request (the multipart audio, the
    /// phrases, and a key sent as a custom header, which .NET does not strip the way it strips an Authorization header)
    /// repeated at wherever the answer pointed. The answer comes back as it is and reads as a failure, so the dictation is
    /// decoded on this PC, as cleanup's transport does.
    /// </summary>
    internal static HttpMessageHandler CreateNetworkHandler() => new HttpClientHandler { AllowAutoRedirect = false };

    // The sign-in step: the credential asked for the Cognitive Services scope, under the attempt's token, so the one
    // deadline and the caller's cancellation end a wait inside the credential (an Azure CLI call queued behind another).
    private async ValueTask<string> AcquireTokenAsync(
        AzureCredentialRequest request, CancellationToken cancellationToken)
    {
        var credential = _credentialFactory(request);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([CognitiveServicesScope]),
            cancellationToken).ConfigureAwait(false);
        return token.Token;
    }

    public async Task<MaiTranscribeAttemptResult> TryTranscribeAsync(
        CapturedAudio audio,
        MaiTranscribeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || audio.IsEmpty)
        {
            return MaiTranscribeAttemptResult.Fallback("disabled");
        }

        if (!MaiTranscribeEndpointResolver.TryResolve(
                options.MaiEndpointOverride,
                options.FoundryEndpoint,
                out var requestUri,
                out var endpointError) ||
            requestUri is null)
        {
            return MaiTranscribeAttemptResult.Fallback(endpointError ?? "endpoint-unset");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();

        // One deadline for the whole attempt, beside the caller's cancellation: the credential, both sends and the answer
        // all run under this token, so no step can outlast it and the retained audio is always free to decode on this PC.
        using var deadline = new CancellationTokenSource(_deadline, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = bounded.Token;

        try
        {
            var wavBytes = MaiWavEncoder.EncodePcm16Wav(audio.Samples, audio.SampleRate);
            var useEnhancedMode = !_fastTranscriptionFallbackHosts.ContainsKey(requestUri.Host);
            var sent = await SendMultipartAsync(
                requestUri,
                options,
                wavBytes,
                BuildDefinitionJson(options, includeEnhancedMode: useEnhancedMode),
                token).ConfigureAwait(false);

            if (sent.HandedOver &&
                sent.StatusCode == 400 &&
                useEnhancedMode &&
                sent.Body.Contains("Enhanced mode", StringComparison.OrdinalIgnoreCase))
            {
                _fastTranscriptionFallbackHosts[requestUri.Host] = true;
                _logger.LogInformation(
                    "MAI-Transcribe-2 enhancedMode is not yet enabled in this resource's region; retrying with Azure Fast Transcription and phraseList biasing.");
                sent = await SendMultipartAsync(
                    requestUri,
                    options,
                    wavBytes,
                    BuildDefinitionJson(options, includeEnhancedMode: false),
                    token).ConfigureAwait(false);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (!sent.HandedOver)
            {
                // Nothing was sent: the AI permission the phrases were admitted with no longer holds.
                _logger.LogInformation(
                    "MAI-Transcribe-2 request was not sent after {ElapsedMs} ms: a word pack's AI permission changed after this dictation was admitted.",
                    (long)elapsed.TotalMilliseconds);
                return MaiTranscribeAttemptResult.Fallback("library-scope", elapsed);
            }

            if (sent.StatusCode is < 200 or >= 300)
            {
                _logger.LogWarning(
                    "MAI-Transcribe-2 request returned HTTP {Status} after {ElapsedMs} ms.",
                    sent.StatusCode,
                    (long)elapsed.TotalMilliseconds);
                return MaiTranscribeAttemptResult.Fallback($"http-{sent.StatusCode}", elapsed);
            }

            // A success status is not yet an answer: a body that is not a transcript response (a gateway's HTML page, an
            // object with none of the members Scribe reads) is a failure, so the retained audio is decoded on this PC
            // instead of the dictation being reported as silence. A recognized response that heard nothing is not.
            if (!TryParseTranscriptResponse(sent.Body, out var transcript))
            {
                _logger.LogWarning(
                    "MAI-Transcribe-2 answered HTTP {Status} with a response Scribe does not recognize after {ElapsedMs} ms.",
                    sent.StatusCode,
                    (long)elapsed.TotalMilliseconds);
                return MaiTranscribeAttemptResult.Fallback("malformed-response", elapsed);
            }

            _logger.LogDebug(
                "MAI-Transcribe-2 recognized {AudioMs} ms of audio in {ElapsedMs} ms ({Chars} characters, {Phrases} bias phrase(s)).",
                (int)audio.Duration.TotalMilliseconds,
                (long)elapsed.TotalMilliseconds,
                transcript.Length,
                options.PhraseList?.Count ?? 0);

            return MaiTranscribeAttemptResult.Success(transcript, elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The caller's cancellation outranks whatever a credential or the transport made of it (a sign-in can wrap
            // its own cancellation in another exception).
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (deadline.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "MAI-Transcribe-2 did not finish within {DeadlineMs} ms, credentials included.",
                    (long)_deadline.TotalMilliseconds);
                return MaiTranscribeAttemptResult.Fallback("timeout", elapsed);
            }

            var failureShape = FailureShape.Describe(ex);
            _logger.LogWarning(
                "MAI-Transcribe-2 request failed after {ElapsedMs} ms ({Failure}).",
                (long)elapsed.TotalMilliseconds,
                failureShape);
            return MaiTranscribeAttemptResult.Fallback(failureShape, elapsed);
        }
    }

    // One send's outcome. HandedOver is false when the admission point refused it, so nothing left this PC.
    private readonly record struct Sent(bool HandedOver, int StatusCode, string Body);

    private async Task<Sent> SendMultipartAsync(
        Uri requestUri,
        MaiTranscribeOptions options,
        byte[] wavBytes,
        string definitionJson,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            var key = options.ApiKey.Trim();
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
            request.Headers.TryAddWithoutValidation("api-key", key);
        }
        else
        {
            var credRequest = new AzureCredentialRequest(
                options.AuthMode,
                options.TenantId,
                options.SubscriptionId,
                options.ClientId,
                options.ClientSecret);
            var bearer = await AcquireTokenAsync(credRequest, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var multipart = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audioContent, "audio", "audio.wav");

        var definitionContent = new StringContent(definitionJson, Encoding.UTF8, "application/json");
        multipart.Add(definitionContent, "definition");
        request.Content = multipart;

        // The last step before the request leaves, and where the admitted library scope is judged: a permission withdrawn
        // while the credential was being acquired, or between the first send and its retry, is seen here, and the phrases
        // already copied into the request go nowhere.
        Task<HttpResponseMessage>? sending = null;
        if (!HandOff(options, () => sending = StartSend(request, cancellationToken)))
        {
            return new Sent(HandedOver: false, StatusCode: 0, Body: string.Empty);
        }

        using var response = await sending!.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new Sent(HandedOver: true, StatusCode: (int)response.StatusCode, Body: body);
    }

    // With library words in the request, runs the send only while the published scope still covers the one the words were
    // admitted with, under the source's permission gate (contract 2.10: the send is started and not waited for, because the
    // gate is held meanwhile). Without any, there is no library permission to judge.
    private static bool HandOff(MaiTranscribeOptions options, Action send)
    {
        if (options.PhraseAdmission is not { } admission)
        {
            send();
            return true;
        }

        return admission.Source.TryHandOff(admission.Scope, send);
    }

    // Never throws inside the permission gate: a send that fails to start fails its own task instead.
    private Task<HttpResponseMessage> StartSend(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }

    public static string BuildDefinitionJson(MaiTranscribeOptions options, bool includeEnhancedMode = true)
    {
        ArgumentNullException.ThrowIfNull(options);

        var style = options.Style == MaiTranscribeStyle.Verbatim ? "verbatim" : "clean";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (includeEnhancedMode)
            {
                writer.WritePropertyName("enhancedMode");
                writer.WriteStartObject();
                writer.WriteBoolean("enabled", true);
                writer.WriteString("model", TranscriptionModelCatalog.MaiTranscribe2ModelName);
                writer.WritePropertyName("modelOptions");
                writer.WriteStartObject();
                writer.WriteString("transcribeStyle", style);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            if (options.PhraseList is { Count: > 0 } phrases)
            {
                writer.WritePropertyName("phraseList");
                writer.WriteStartObject();
                writer.WritePropertyName("phrases");
                writer.WriteStartArray();
                foreach (var phrase in phrases)
                {
                    if (!string.IsNullOrWhiteSpace(phrase))
                    {
                        writer.WriteStringValue(phrase.Trim());
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly string[] PhraseArrayMembers = ["combinedPhrases", "phrases"];
    private static readonly string[] TextMembers = ["text", "DisplayText"];

    /// <summary>
    /// Reads the transcript out of the body of a successful answer. False when the body is not a transcript response Scribe
    /// recognizes: it is not JSON, not an object, or an object with none of the transcript members Scribe reads in the shape
    /// it expects. That is a failure, for the caller to decode on this PC, and never an empty dictation. True with an empty
    /// <paramref name="transcript"/> is a recognized answer that heard nothing.
    /// </summary>
    public static bool TryParseTranscriptResponse(string? json, out string transcript)
    {
        transcript = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // A member counts as recognized when it has the shape the service documents, whether or not it holds words: the
            // first one that holds any is the transcript, and when none does, a recognized member means it heard nothing.
            var recognized = false;
            foreach (var member in PhraseArrayMembers)
            {
                if (!TryJoinPhraseArray(root, member, out var joined))
                {
                    continue;
                }

                recognized = true;
                if (joined.Length > 0)
                {
                    transcript = joined;
                    return true;
                }
            }

            foreach (var member in TextMembers)
            {
                if (!root.TryGetProperty(member, out var node) || node.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                recognized = true;
                var text = node.GetString()?.Trim() ?? string.Empty;
                if (text.Length > 0)
                {
                    transcript = text;
                    return true;
                }
            }

            return recognized;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The words of a phrase array in the shape the service documents: an array whose every element is an object with a
    // string text. False when the member is absent or shaped otherwise. True with an empty result when the array holds no
    // words (empty text, or no elements at all), which is a recognized answer that heard nothing.
    private static bool TryJoinPhraseArray(JsonElement root, string propertyName, out string result)
    {
        result = string.Empty;
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var textProp) ||
                textProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (textProp.GetString()?.Trim() is { Length: > 0 } part)
            {
                parts.Add(part);
            }
        }

        result = string.Join(' ', parts);
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
