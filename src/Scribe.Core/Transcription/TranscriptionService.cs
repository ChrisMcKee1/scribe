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
    private const int MaxAutoThreads = 8;

    private readonly ModelLocator _locator;
    private readonly TranscriptionOptions _options;
    private readonly ILogger<TranscriptionService> _logger;
    private readonly object _gate = new();

    private OfflineRecognizer? _recognizer;
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

            var models = _locator.ResolveOrThrow();
            var threads = ResolveThreadCount(_options.NumThreads);

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Tokens = models.TokensPath;
            config.ModelConfig.Transducer.Encoder = models.EncoderPath;
            config.ModelConfig.Transducer.Decoder = models.DecoderPath;
            config.ModelConfig.Transducer.Joiner = models.JoinerPath;
            config.ModelConfig.ModelType = ModelType;
            config.ModelConfig.NumThreads = threads;
            config.ModelConfig.Provider = "cpu";
            // Defaults from the ctor are already correct: DecodingMethod = "greedy_search",
            // FeatConfig.SampleRate = 16000, FeatConfig.FeatureDim = 80.

            var sw = Stopwatch.StartNew();
            _recognizer = new OfflineRecognizer(config);
            sw.Stop();

            _logger.LogInformation(
                "Loaded Parakeet recognizer ({Threads} threads) from {Directory} in {ElapsedMs} ms",
                threads, models.Directory, sw.ElapsedMilliseconds);
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

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(audio.SampleRate, audio.Samples);
            recognizer.Decode(stream);

            sw.Stop();
            var text = stream.Result.Text?.Trim() ?? string.Empty;
            var result = new TranscriptionResult(text, audio.Duration, sw.Elapsed);

            _logger.LogDebug(
                "Decoded {AudioMs} ms of audio in {DecodeMs} ms (RTF {Rtf:F2}): \"{Text}\"",
                (int)audio.Duration.TotalMilliseconds, sw.ElapsedMilliseconds,
                result.RealTimeFactor, text);

            return result;
        }
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
        }
    }
}
