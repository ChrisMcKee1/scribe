using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using SherpaOnnx;

namespace Scribe.Core.Transcription;

/// <inheritdoc cref="ITranscriptionService"/>
public sealed class TranscriptionService : ITranscriptionService
{
    private const string ModelType = "nemo_transducer";

    /// <summary>
    /// Mel filterbank size Parakeet TDT was trained with. sherpa-onnx defaults to 80, which is the
    /// Icefall/Zipformer convention; the NeMo FastConformer models use 128.
    /// </summary>
    private const int NemoFeatureDim = 128;

    private const int MaxAutoThreads = 8;
    private const int WarmUpSampleCount = 8_000; // 0.5 s at 16 kHz

    private readonly ModelLocator _locator;
    private readonly TranscriptionOptions _options;
    private readonly ILogger<TranscriptionService> _logger;
    private readonly object _gate = new();

    private OfflineRecognizer? _recognizer;
    private string? _activeModelId;
    private bool _disposed;

    public TranscriptionService(
        ModelLocator locator,
        IOptions<TranscriptionOptions> options,
        ILogger<TranscriptionService> logger)
    {
        _locator = locator;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsReady => Volatile.Read(ref _recognizer) is not null;

    public void Initialize()
    {
        if (IsReady) return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recognizer is not null) return;

            var model = TranscriptionModelCatalog.Resolve(_options.ModelId);
            var models = _locator.ResolveOrDefault(model, out var usedFallback);
            if (!models.AsrComplete)
            {
                var missing = string.Join(", ", models.MissingAsrFiles());
                throw new FileNotFoundException(
                    $"Speech model files were not found. Missing: {missing}. " +
                    "Run scripts/Download-Models.ps1 or install the selected model in Settings.");
            }
            if (usedFallback)
            {
                _logger.LogWarning(
                    "Selected speech model {Model} is unavailable; using bundled Parakeet.", model.Id);
                model = TranscriptionModelCatalog.Resolve(TranscriptionModelCatalog.DefaultId);
            }
            var threads = ResolveThreadCount(_options.NumThreads);

            var decoding = TranscriptionDecoding.Resolve(
                _options.DecodingMethod, model.Architecture, _options.AllowUnsafeDecodingMethod);
            if (decoding.Overridden)
            {
                _logger.LogWarning(
                    "Decoding method {Requested} is not safe for {Model}; decoding greedily instead.",
                    _options.DecodingMethod, model.DisplayName);
            }

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Tokens = models.TokensPath;
            ConfigureModel(ref config, model, models);
            config.ModelConfig.NumThreads = threads;
            config.ModelConfig.Provider = "cpu";
            config.DecodingMethod = decoding.Method;
            config.MaxActivePaths = Math.Max(1, _options.MaxActivePaths);

            // Parakeet TDT is trained on 128 mel bins, not the sherpa-onnx default of 80. The
            // runtime corrects this from the model's own metadata, so leaving it wrong is currently
            // harmless, but the config we hand it should still describe the model we are loading:
            // a future reordering that reads FeatureDim before the runtime fixes it would silently
            // produce garbage features rather than fail. Moonshine does its own preprocessing and
            // ignores this entirely.
            if (model.Architecture == TranscriptionModelArchitecture.NemoTransducer)
            {
                config.FeatConfig.FeatureDim = NemoFeatureDim;
            }

            var sw = Stopwatch.StartNew();
            _recognizer = new OfflineRecognizer(config);
            _activeModelId = model.Id;
            sw.Stop();

            _logger.LogInformation(
                "Loaded {Model} recognizer ({Threads} threads, {Method}) from {Directory} in {ElapsedMs} ms",
                model.DisplayName, threads, config.DecodingMethod, models.Directory, sw.ElapsedMilliseconds);

            WarmUp(_recognizer);
        }
    }

    internal static void ConfigureModel(
        ref OfflineRecognizerConfig config,
        TranscriptionModel model,
        ModelSet models)
    {
        if (model.Architecture == TranscriptionModelArchitecture.Moonshine)
        {
            config.ModelConfig.Moonshine.Preprocessor = Path.Combine(models.Directory, "preprocess.onnx");
            config.ModelConfig.Moonshine.Encoder = Path.Combine(models.Directory, "encode.int8.onnx");
            config.ModelConfig.Moonshine.UncachedDecoder =
                Path.Combine(models.Directory, "uncached_decode.int8.onnx");
            config.ModelConfig.Moonshine.CachedDecoder =
                Path.Combine(models.Directory, "cached_decode.int8.onnx");
            return;
        }

        config.ModelConfig.Transducer.Encoder = models.EncoderPath;
        config.ModelConfig.Transducer.Decoder = models.DecoderPath;
        config.ModelConfig.Transducer.Joiner = models.JoinerPath;
        config.ModelConfig.ModelType = ModelType;
    }

    /// <summary>
    /// Runs one throwaway decode on a short buffer of near-silence so ONNX Runtime allocates its
    /// arenas and JITs the graph up front. Without this the very first real utterance pays that
    /// one-off cost and reports a misleadingly high latency / RTF. Best-effort: a warm-up failure
    /// must never prevent the recognizer from being used.
    /// </summary>
    private void WarmUp(OfflineRecognizer recognizer)
    {
        try
        {
            // 0.5 s of very quiet dither at 16 kHz exercises the full encoder→decoder→joiner path.
            var samples = new float[WarmUpSampleCount];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = ((i & 1) == 0 ? 1 : -1) * 1e-4f;

            var sw = Stopwatch.StartNew();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16_000, samples);
            recognizer.Decode(stream);
            sw.Stop();

            _logger.LogInformation("Recognizer warm-up decode completed in {ElapsedMs} ms.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recognizer warm-up decode failed; first real decode may be slower.");
        }
    }

    public TranscriptionResult Transcribe(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty) return TranscriptionResult.Empty;

        if (!IsReady) Initialize();

        var sw = Stopwatch.StartNew();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var recognizer = _recognizer
                ?? throw new InvalidOperationException("Recognizer is not initialized.");

            // Long captures decode in bounded chunks (see TranscriptionChunker) because one long
            // decode permanently pins the arena at its high-water mark. Chunks are decoded one at
            // a time on purpose: the batch Decode(IEnumerable) overload runs them as a single
            // padded batch through the encoder, which multiplies the very allocation the chunking
            // exists to cap.
            var spans = TranscriptionChunker.Plan(audio.Samples, audio.SampleRate);
            string text;
            if (spans.Count == 1)
            {
                text = DecodeSpan(recognizer, audio, spans[0]);
            }
            else
            {
                _logger.LogInformation(
                    "Capture of {Seconds:F1}s exceeds {Max}s; decoding as {Chunks} chunks.",
                    audio.Duration.TotalSeconds, TranscriptionChunker.MaxChunkSeconds, spans.Count);

                var parts = new List<string>(spans.Count);
                foreach (var span in spans)
                {
                    var part = DecodeSpan(recognizer, audio, span);
                    if (part.Length > 0) parts.Add(part);
                }

                text = string.Join(' ', parts);
            }

            sw.Stop();
            var result = new TranscriptionResult(text, audio.Duration, sw.Elapsed, _activeModelId);

            _logger.LogDebug(
                "Decoded {AudioMs} ms of audio in {DecodeMs} ms (RTF {Rtf:F2}): \"{Text}\"",
                (int)audio.Duration.TotalMilliseconds, sw.ElapsedMilliseconds,
                result.RealTimeFactor, text);

            return result;
        }
    }

    private static string DecodeSpan(
        OfflineRecognizer recognizer, CapturedAudio audio, (int Start, int Length) span)
    {
        var samples = span.Start == 0 && span.Length == audio.Samples.Length
            ? audio.Samples
            : audio.Samples.AsSpan(span.Start, span.Length).ToArray();

        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(audio.SampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text?.Trim() ?? string.Empty;
    }

    private static int ResolveThreadCount(int configured)
    {
        if (configured > 0) return configured;
        return Math.Clamp(Environment.ProcessorCount / 2, 1, MaxAutoThreads);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _recognizer?.Dispose();
            _recognizer = null;
            _activeModelId = null;
        }
    }
}
