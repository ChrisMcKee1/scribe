using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class OllamaModelDownloaderTests
{
    [Theory]
    [InlineData(" gemma4:12b ", "gemma4:12b")]
    [InlineData("deepseek-r1", "deepseek-r1")]
    [InlineData("VicRodger27/Writex:4b", "VicRodger27/Writex:4b")]
    [InlineData("ollama run VicRodger27/Writex:4b", "VicRodger27/Writex:4b")]
    public async Task Downloads_through_the_SDK_to_Ollama_only_and_reports_its_file_progress(string input, string model)
    {
        string? body = null;
        var requests = 0;
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler(async (request, ct) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("localhost", request.RequestUri!.Host);
            Assert.Equal(11434, request.RequestUri.Port);
            Assert.Equal("/api/pull", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            body = await request.Content!.ReadAsStringAsync(ct);
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                {"status":"pulling manifest"}
                {"status":"pulling sha256:abc","digest":"sha256:abc","total":100,"completed":40}
                {"status":"verifying sha256 digest"}
                {"status":"writing manifest"}
                {"status":"success"}
                """);
        }));
        var progress = new CaptureProgress();

        await downloader.DownloadAsync(input, progress);

        Assert.Equal(1, requests);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal(model, sent.RootElement.GetProperty("model").GetString());
        if (sent.RootElement.TryGetProperty("stream", out var streaming))
        {
            Assert.True(streaming.GetBoolean());
        }

        if (sent.RootElement.TryGetProperty("insecure", out var insecure))
        {
            Assert.False(insecure.GetBoolean());
        }

        Assert.Equal(
            [OllamaDownloadStage.Preparing, OllamaDownloadStage.Downloading, OllamaDownloadStage.Verifying,
                OllamaDownloadStage.Installing, OllamaDownloadStage.Completed],
            progress.Values.Select(value => value.Stage));
        var file = progress.Values[1];
        Assert.Equal(40, file.CompletedBytes);
        Assert.Equal(100, file.TotalBytes);
        Assert.Equal(40d, file.Percent);
        Assert.Null(progress.Values[0].Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://ollama.com/library/gemma4")]
    [InlineData("gemma4:cloud")]
    [InlineData("gemma4:31b-cloud")]
    [InlineData("gemma4:12b; stop")]
    [InlineData("ollama run gemma4:cloud")]
    [InlineData("ollama run VicRodger27/Writex:4b --verbose")]
    public async Task Invalid_or_cloud_names_send_nothing(string model)
    {
        var requests = 0;
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"status":"success"}"""));
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(model));

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task A_stream_ending_without_success_is_not_a_completed_download()
    {
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                {"status":"pulling sha256:abc","digest":"sha256:abc","total":100,"completed":100}
                {"status":"verifying sha256 digest"}
                """))));
        var progress = new CaptureProgress();

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync("gemma4:12b", progress));

        Assert.DoesNotContain(progress.Values, value => value.Stage == OllamaDownloadStage.Completed);
    }

    [Theory]
    [InlineData("{\"error\":\"private failure\"}")]
    [InlineData("not json")]
    public async Task A_stream_error_is_not_a_completed_download(string answer)
    {
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, answer))));
        var progress = new CaptureProgress();

        var failure = await Record.ExceptionAsync(() => downloader.DownloadAsync("gemma4:12b", progress));

        Assert.NotNull(failure);
        Assert.DoesNotContain(progress.Values, value => value.Stage == OllamaDownloadStage.Completed);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "ResponseError")]
    [InlineData(HttpStatusCode.BadRequest, "OllamaException")]
    [InlineData(HttpStatusCode.NotFound, "HttpRequestException")]
    public async Task SDK_stream_and_HTTP_error_shapes_report_explicit_safe_failure_not_success(
        HttpStatusCode status, string exceptionType)
    {
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(status, """{"error":"synthetic private registry message"}"""))));
        var progress = new CaptureProgress();

        var failure = await Record.ExceptionAsync(() => downloader.DownloadAsync("gemma4:12b", progress));

        Assert.NotNull(failure);
        Assert.Equal(exceptionType, failure.GetType().Name);
        Assert.DoesNotContain(progress.Values, value => value.Stage == OllamaDownloadStage.Completed);
        Assert.Equal(
            status == HttpStatusCode.NotFound
                ? "Ollama couldn't find that model and tag. Copy the exact name and tag from the catalog's run command; some publishers don't offer latest."
                : "Ollama couldn't finish the download. Check that it's open and you have enough disk space, then try again.",
            OllamaModelDownload.Failure(failure));
    }

    [Fact]
    public async Task A_missing_manifest_in_a_successful_HTTP_stream_names_the_model_tag_problem_not_a_device_failure()
    {
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                {"status":"pulling manifest"}
                {"error":"pull model manifest: file does not exist"}
                """))));
        var progress = new CaptureProgress();

        var failure = await Record.ExceptionAsync(() => downloader.DownloadAsync("VicRodger27/Writex", progress));

        Assert.NotNull(failure);
        Assert.Equal("ResponseError", failure.GetType().Name);
        Assert.DoesNotContain(progress.Values, value => value.Stage == OllamaDownloadStage.Completed);
        Assert.Equal(
            "Ollama couldn't find that model and tag. Copy the exact name and tag from the catalog's run command; some publishers don't offer latest.",
            OllamaModelDownload.Failure(failure));
    }

    [Fact]
    public async Task Cancellation_reaches_the_request_and_never_reports_success()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"status":"success"}""");
        }));
        using var cancellation = new CancellationTokenSource();
        var progress = new CaptureProgress();
        var download = downloader.DownloadAsync("gemma4:12b", progress, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(progress.Values, value => value.Stage == OllamaDownloadStage.Completed);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            await Record.ExceptionAsync(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(download.IsCompleted, "The scripted request must not outlive its test.");
        }
    }

    [Fact]
    public async Task Cancellation_before_admission_sends_nothing()
    {
        var requests = 0;
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"status":"success"}"""));
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync("gemma4:12b", cancellationToken: cancellation.Token));

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Cancellation_also_stops_reading_a_download_after_its_headers_and_first_progress()
    {
        using var body = new WaitingDownloadStream();
        using var downloader = new OllamaModelDownloader(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        using var cancellation = new CancellationTokenSource();
        var download = downloader.DownloadAsync("gemma4:12b", cancellationToken: cancellation.Token);
        try
        {
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => download.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            body.Release();
            cancellation.Cancel();
            await Record.ExceptionAsync(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(download.IsCompleted, "The scripted body read must not outlive its test.");
        }
    }

    [Fact]
    public async Task A_logger_that_throws_cannot_fail_a_download()
    {
        using var downloader = new OllamaModelDownloader(
            new ScriptedHttpHandler((_, _) =>
                Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"status":"success"}"""))),
            new ThrowingLogger());

        await downloader.DownloadAsync("gemma4:12b");
    }

    private sealed class CaptureProgress : IProgress<OllamaDownloadProgress>
    {
        public List<OllamaDownloadProgress> Values { get; } = [];
        public void Report(OllamaDownloadProgress value) => Values.Add(value);
    }

    private sealed class WaitingDownloadStream : Stream
    {
        private readonly TaskCompletionSource<int> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _prefix = new(System.Text.Encoding.UTF8.GetBytes(
            "{\"status\":\"pulling sha256:abc\",\"digest\":\"sha256:abc\",\"total\":100,\"completed\":1}\n"));

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length)
            {
                return ValueTask.FromResult(_prefix.Read(buffer.Span));
            }

            Waiting.TrySetResult();
            return WaitAsync(cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async ValueTask<int> WaitAsync(CancellationToken cancellationToken) =>
            await _release.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult(0);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release();
                _prefix.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => throw new InvalidOperationException("logger unavailable");
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger unavailable");
    }
}
