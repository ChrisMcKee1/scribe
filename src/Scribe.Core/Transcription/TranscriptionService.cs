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

    private readonly Func<ISpeechRecognizer> _load;
    private readonly ILogger<TranscriptionService> _logger;

    // Every native call happens under this gate: load, warm-up, decode, unload and dispose. Holding it across
    // ensure-ready AND use is what stops an idle Unload from landing between the two, and holding it in Dispose is
    // what stops shutdown from freeing the recognizer under a decode that is still running.
    private readonly object _gate = new();

    private ISpeechRecognizer? _recognizer;
    private bool _disposed;

    public TranscriptionService(
        ModelLocator locator,
        IOptions<TranscriptionOptions> options,
        ILogger<TranscriptionService> logger)
    {
        var resolved = options.Value;
        _logger = logger;
        _load = () => LoadSherpaRecognizer(locator, resolved);
    }

    /// <summary>Test seam: supplies the recognizer instead of loading sherpa-onnx.</summary>
    internal TranscriptionService(Func<ISpeechRecognizer> load, ILogger<TranscriptionService> logger)
    {
        _load = load;
        _logger = logger;
    }

    public bool IsReady => Volatile.Read(ref _recognizer) is not null;

    public void Initialize()
    {
        if (IsReady) return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureLoaded();
        }
    }

    // Caller holds _gate. Returns the time spent loading, zero when the recognizer was already resident, so a cold
    // start is reported apart from the decode it delayed instead of being folded into it.
    private TimeSpan EnsureLoaded()
    {
        if (_recognizer is not null) return TimeSpan.Zero;

        var started = Stopwatch.GetTimestamp();
        var recognizer = _load();
        Volatile.Write(ref _recognizer, recognizer);
        return Stopwatch.GetElapsedTime(started);
    }

    private ISpeechRecognizer LoadSherpaRecognizer(ModelLocator locator, TranscriptionOptions options)
    {
        var model = TranscriptionModelCatalog.Resolve(options.ModelId);
        var models = locator.ResolveOrDefault(model, out var usedFallback);
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
        var threads = ResolveThreadCount(options.NumThreads);

        var decoding = TranscriptionDecoding.Resolve(
            options.DecodingMethod, model.Architecture, options.AllowUnsafeDecodingMethod);
        if (decoding.Overridden)
        {
            _logger.LogWarning(
                "Decoding method {Requested} is not safe for {Model}; decoding greedily instead.",
                options.DecodingMethod, model.DisplayName);
        }

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Tokens = models.TokensPath;
        ConfigureModel(ref config, model, models);
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = decoding.Method;
        config.MaxActivePaths = Math.Max(1, options.MaxActivePaths);

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
        var recognizer = new OfflineRecognizer(config);
        sw.Stop();

        _logger.LogInformation(
            "Loaded {Model} recognizer ({Threads} threads, {Method}) from {Directory} in {ElapsedMs} ms",
            model.DisplayName, threads, config.DecodingMethod, models.Directory, sw.ElapsedMilliseconds);

        WarmUp(recognizer);
        return new SherpaRecognizer(recognizer, model.Id);
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

    public TranscriptionResult Transcribe(CapturedAudio audio) =>
        Transcribe(audio, CancellationToken.None);

    public TranscriptionResult Transcribe(CapturedAudio audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty) return TranscriptionResult.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        var waitStarted = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            var waited = Stopwatch.GetElapsedTime(waitStarted);

            // Both checks come after the gate and before any load: a call that lost the race with Dispose must not
            // bring the engine back to life, and a caller that gave up while queued behind another decode should not
            // pay for a load it no longer wants.
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            // Loading under the same gate as the decode makes ensure-ready plus use one step: an idle Unload now lands
            // before this dictation (which reloads) or after it, never in between. Checking readiness outside the gate
            // is what let an Unload fail the dictation with "Recognizer is not initialized."
            var loadTime = EnsureLoaded();
            var recognizer = _recognizer!;

            var decodeTimer = Stopwatch.StartNew();

            // Long captures decode in bounded chunks (see TranscriptionChunker) because one long
            // decode permanently pins the arena at its high-water mark. Chunks are decoded one at
            // a time on purpose: the batch Decode(IEnumerable) overload runs them as a single
            // padded batch through the encoder, which multiplies the very allocation the chunking
            // exists to cap.
            var spans = TranscriptionChunker.Plan(audio.Samples, audio.SampleRate);
            string text;
            if (spans.Count == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    // The native Decode cannot be interrupted, so a chunk boundary is the only safe place to stop.
                    // Stopping throws instead of returning what was decoded so far: a partial transcript must never
                    // be mistaken for a complete one.
                    cancellationToken.ThrowIfCancellationRequested();
                    var part = DecodeSpan(recognizer, audio, span);
                    if (part.Length > 0) parts.Add(part);
                }

                text = string.Join(' ', parts);
            }

            decodeTimer.Stop();
            var result = new TranscriptionResult(text, audio.Duration, decodeTimer.Elapsed, recognizer.ModelId);

            // Shape only. The recognized text itself must never reach the log (PRIVACY.md), and leaving it out of the
            // template alone would not be enough: structured sinks keep every argument, rendered or not.
            _logger.LogDebug(
                "Decoded {AudioMs} ms of audio in {DecodeMs} ms (RTF {Rtf:F2}, {Chunks} chunk(s), {Chars} characters); " +
                "model load {LoadMs} ms, waited {WaitMs} ms for the engine.",
                (int)audio.Duration.TotalMilliseconds, decodeTimer.ElapsedMilliseconds, result.RealTimeFactor,
                spans.Count, text.Length, (long)loadTime.TotalMilliseconds, (long)waited.TotalMilliseconds);

            return result;
        }
    }

    private static string DecodeSpan(
        ISpeechRecognizer recognizer, CapturedAudio audio, (int Start, int Length) span)
    {
        var samples = span.Start == 0 && span.Length == audio.Samples.Length
            ? audio.Samples
            : audio.Samples.AsSpan(span.Start, span.Length).ToArray();

        return recognizer.Decode(samples, audio.SampleRate);
    }

    private static int ResolveThreadCount(int configured)
    {
        if (configured > 0) return configured;
        return Math.Clamp(Environment.ProcessorCount / 2, 1, MaxAutoThreads);
    }

    public void Unload()
    {
        lock (_gate)
        {
            if (_disposed || _recognizer is null) return;
            _recognizer.Dispose();
            Volatile.Write(ref _recognizer, null);
            _logger.LogInformation("Recognizer unloaded; it will reload on the next dictation.");
        }
    }

    public void Dispose()
    {
        // Takes the same gate as a decode, so disposal during shutdown waits for a decode that is still running (at
        // most the current chunk, when the caller has canceled) instead of freeing the recognizer underneath it.
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _recognizer?.Dispose();
            Volatile.Write(ref _recognizer, null);
        }
    }

    private sealed class SherpaRecognizer(OfflineRecognizer recognizer, string modelId) : ISpeechRecognizer
    {
        public string ModelId { get; } = modelId;

        public string Decode(float[] samples, int sampleRate)
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            recognizer.Decode(stream);
            return stream.Result.Text?.Trim() ?? string.Empty;
        }

        public void Dispose() => recognizer.Dispose();
    }
}
