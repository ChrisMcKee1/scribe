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
    IReadOnlyList<string>? PhraseList = null)
{
    public static MaiTranscribeOptions Disabled { get; } = new(false, null, null, null);

    public static MaiTranscribeOptions FromSettings(
        AppSettings settings,
        bool isPackaged,
        PerfFlags? perfFlags = null,
        IEnumerable<DictionaryEntry>? personalDictionary = null,
        ILibraryVocabularySource? librarySource = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var enabled = string.Equals(
                settings.TranscriptionModelId?.Trim(),
                TranscriptionModelCatalog.MaiTranscribe2Id,
                StringComparison.OrdinalIgnoreCase) &&
            InstallerFeatureGate.CanUseMaiTranscribe(isPackaged, perfFlags);
        if (!enabled)
        {
            return Disabled;
        }

        var tenantId = settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal
            ? settings.AiCleanupAzureTenantId
            : AzureSubscriptionSelection.ResolveTenantId(
                settings.AiCleanupAzureSubscriptionId,
                settings.AiCleanupAzureSubscriptionTenantId,
                settings.AiCleanupAzureTenantId);

        var phrases = settings.ApplyPostProcessing
            ? MaiTranscribePhraseList.Build(personalDictionary, librarySource)
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
            PhraseList: phrases);
    }
}

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
/// enabled dictionary entries and AI-permitted word packs, enforcing the same single-line 100-character
/// vocabulary rule and library hand-off admission gate as AI cleanup.
/// </summary>
public static class MaiTranscribePhraseList
{
    public const int MaxPhrases = 500;

    public static IReadOnlyList<string> Build(
        IEnumerable<DictionaryEntry>? personalEntries,
        ILibraryVocabularySource? librarySource = null)
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

        if (librarySource is not null)
        {
            var current = librarySource.Current;
            if (current.AiEntries.Count > 0)
            {
                librarySource.TryHandOff(current.AiScope, () =>
                {
                    foreach (var entry in current.AiEntries)
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
                });
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
public sealed class MaiTranscribeClient : IMaiTranscribeClient, IDisposable
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<AzureCredentialRequest, ValueTask<string>> _tokenProvider;
    private readonly ILogger<MaiTranscribeClient> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _fastTranscriptionFallbackHosts =
        new(StringComparer.OrdinalIgnoreCase);

    public MaiTranscribeClient(
        ILogger<MaiTranscribeClient> logger,
        PerfFlags? perfFlags = null,
        HttpMessageHandler? handler = null)
        : this(logger, perfFlags, handler, tokenProvider: null)
    {
    }

    internal MaiTranscribeClient(
        ILogger<MaiTranscribeClient> logger,
        PerfFlags? perfFlags,
        HttpMessageHandler? handler,
        Func<AzureCredentialRequest, ValueTask<string>>? tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _ownsHttpClient = handler is null;
        _httpClient = handler is null
            ? new HttpClient { Timeout = DefaultTimeout }
            : new HttpClient(handler, disposeHandler: false) { Timeout = DefaultTimeout };
        _tokenProvider = tokenProvider ?? (async request =>
        {
            var credential = AzureCredentialFactory.Create(request);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([CognitiveServicesScope]),
                CancellationToken.None).ConfigureAwait(false);
            return token.Token;
        });
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

        try
        {
            var wavBytes = MaiWavEncoder.EncodePcm16Wav(audio.Samples, audio.SampleRate);
            var useEnhancedMode = !_fastTranscriptionFallbackHosts.ContainsKey(requestUri.Host);
            var (statusCode, body) = await SendMultipartAsync(
                requestUri,
                options,
                wavBytes,
                BuildDefinitionJson(options, includeEnhancedMode: useEnhancedMode),
                cancellationToken).ConfigureAwait(false);

            if (statusCode == 400 &&
                useEnhancedMode &&
                body.Contains("Enhanced mode", StringComparison.OrdinalIgnoreCase))
            {
                _fastTranscriptionFallbackHosts[requestUri.Host] = true;
                _logger.LogInformation(
                    "MAI-Transcribe-2 enhancedMode is not yet enabled in this resource's region; retrying with Azure Fast Transcription and phraseList biasing.");
                (statusCode, body) = await SendMultipartAsync(
                    requestUri,
                    options,
                    wavBytes,
                    BuildDefinitionJson(options, includeEnhancedMode: false),
                    cancellationToken).ConfigureAwait(false);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (statusCode is < 200 or >= 300)
            {
                _logger.LogWarning(
                    "MAI-Transcribe-2 request returned HTTP {Status} after {ElapsedMs} ms.",
                    statusCode,
                    (long)elapsed.TotalMilliseconds);
                return MaiTranscribeAttemptResult.Fallback($"http-{statusCode}", elapsed);
            }

            var transcript = ParseTranscriptResponse(body);
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
            var elapsed = Stopwatch.GetElapsedTime(started);
            var failureShape = FailureShape.Describe(ex);
            _logger.LogWarning(
                "MAI-Transcribe-2 request failed after {ElapsedMs} ms ({Failure}).",
                (long)elapsed.TotalMilliseconds,
                failureShape);
            return MaiTranscribeAttemptResult.Fallback(failureShape, elapsed);
        }
    }

    private async Task<(int StatusCode, string Body)> SendMultipartAsync(
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
            var bearer = await _tokenProvider(credRequest).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var multipart = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audioContent, "audio", "audio.wav");

        var definitionContent = new StringContent(definitionJson, Encoding.UTF8, "application/json");
        multipart.Add(definitionContent, "definition");
        request.Content = multipart;

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, body);
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

    public static string ParseTranscriptResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (TryJoinPhraseArray(root, "combinedPhrases", out var combined))
            {
                return combined;
            }

            if (TryJoinPhraseArray(root, "phrases", out var segmented))
            {
                return segmented;
            }

            if (root.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
            {
                return textNode.GetString()?.Trim() ?? string.Empty;
            }

            if (root.TryGetProperty("DisplayText", out var displayNode) && displayNode.ValueKind == JsonValueKind.String)
            {
                return displayNode.GetString()?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

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
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var textProp) &&
                textProp.ValueKind == JsonValueKind.String &&
                textProp.GetString()?.Trim() is { Length: > 0 } part)
            {
                parts.Add(part);
            }
        }

        if (parts.Count == 0)
        {
            return false;
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
