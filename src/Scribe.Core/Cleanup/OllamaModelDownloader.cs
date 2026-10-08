using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using OllamaSharp.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Cleanup;

public enum OllamaDownloadStage
{
    Preparing,
    Downloading,
    Verifying,
    Installing,
    Completed,
}

public sealed record OllamaDownloadProgress(
    OllamaDownloadStage Stage,
    long CompletedBytes = 0,
    long TotalBytes = 0,
    double? Percent = null);

/// <summary>Explicit model downloads through Ollama's SDK, to its app on this PC only.</summary>
public sealed class OllamaModelDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly OllamaApiClient _client;
    private readonly ILogger _log;
    private CancellationToken _readCancellation;
    private int _downloading;

    public OllamaModelDownloader(ILogger? log = null)
        : this(LocalServerClient.CreateHandler(), log)
    {
    }

    internal OllamaModelDownloader(HttpMessageHandler handler, ILogger? log = null)
    {
        _http = new HttpClient(new OllamaDownloadReadCancellation(handler, () => _readCancellation), disposeHandler: true)
        {
            BaseAddress = new Uri(new Uri(LocalAiServer.OllamaAddress).GetLeftPart(UriPartial.Authority) + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client = new OllamaApiClient(_http);
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Downloads without loading or selecting the model. A stopped stream is not success: Ollama must confirm it.
    /// Cancellation leaves downloaded parts in Ollama, which resumes them on the next request.
    /// </summary>
    public async Task DownloadAsync(
        string model,
        IProgress<OllamaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = OllamaModelDownload.Validate(model);
        if (request.Model is not { } name)
        {
            throw new ArgumentException(request.Error, nameof(model));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _downloading, 1, 0) != 0)
        {
            throw new InvalidOperationException("This downloader already has a model download in progress.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        _readCancellation = timeout.Token;
        var completed = false;
        TryLog("Started");
        try
        {
            await foreach (var response in _client.PullModelAsync(
                new PullModelRequest { Model = name }, timeout.Token).ConfigureAwait(false))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (response is null)
                {
                    continue;
                }

                var stage = Stage(response);
                progress?.Report(new(
                    stage,
                    response.Completed,
                    response.Total,
                    response.Total > 0 ? response.Percent : null));
                if (stage == OllamaDownloadStage.Completed)
                {
                    completed = true;
                    return;
                }
            }

            throw new InvalidDataException("Ollama ended the download without confirming success.");
        }
        finally
        {
            TryLog(completed ? "Completed" : cancellationToken.IsCancellationRequested ? "Canceled" : "Failed");
            _readCancellation = default;
            Volatile.Write(ref _downloading, 0);
        }
    }

    private static OllamaDownloadStage Stage(PullModelResponse response) =>
        response.Status switch
        {
            "success" => OllamaDownloadStage.Completed,
            "verifying sha256 digest" => OllamaDownloadStage.Verifying,
            "writing manifest" or "removing any unused layers" => OllamaDownloadStage.Installing,
            _ => response.Total > 0 ? OllamaDownloadStage.Downloading : OllamaDownloadStage.Preparing,
        };

    private void TryLog(string outcome)
    {
        try
        {
            _log.LogInformation("Ollama model download: {Outcome}.", outcome);
        }
        catch
        {
            // A diagnostics failure must never stop a model download.
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }
}
