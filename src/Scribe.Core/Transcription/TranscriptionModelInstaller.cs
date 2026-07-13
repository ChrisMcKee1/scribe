using System.Diagnostics;
using System.Security.Cryptography;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Transcription;

public interface ITranscriptionModelInstaller
{
    bool IsInstalled(TranscriptionModel model);
    Task InstallAsync(
        TranscriptionModel model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class TranscriptionModelInstaller(AppPaths paths, ModelLocator locator)
    : ITranscriptionModelInstaller
{
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public bool IsInstalled(TranscriptionModel model) => locator.Resolve(model).AsrComplete;

    public async Task InstallAsync(
        TranscriptionModel model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.IsBundled || model.DownloadUri is null ||
            string.IsNullOrWhiteSpace(model.ArchiveSha256) ||
            string.IsNullOrWhiteSpace(model.ArchiveDirectory))
        {
            throw new InvalidOperationException("This speech model is bundled and does not need to be downloaded.");
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stagingRoot = Path.Combine(paths.ModelsDir, $".install-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            var archivePath = Path.Combine(stagingRoot, "model.tar.bz2");
            await DownloadAsync(model, archivePath, progress, cancellationToken).ConfigureAwait(false);
            await VerifyHashAsync(archivePath, model.ArchiveSha256, cancellationToken).ConfigureAwait(false);
            await ExtractAsync(archivePath, stagingRoot, cancellationToken).ConfigureAwait(false);

            var extracted = Path.Combine(stagingRoot, model.ArchiveDirectory);
            var extractedSet = ModelSet.ForDirectory(extracted, model.RequiredFiles);
            if (!extractedSet.AsrComplete)
            {
                throw new InvalidDataException(
                    $"The downloaded model is incomplete. Missing: {string.Join(", ", extractedSet.MissingAsrFiles())}.");
            }

            var destination = Path.Combine(paths.ModelsDir, model.Id);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(extracted, destination);
            progress?.Report(1);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch
            {
                // A cleanup failure must not hide a successful install or the actionable root error.
            }
            _installGate.Release();
        }
    }

    private static async Task DownloadAsync(
        TranscriptionModel model,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(
            model.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        var buffer = new byte[1024 * 128];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            if (expectedLength > 0)
            {
                progress?.Report(Math.Min(0.9, total / (double)expectedLength.Value * 0.9));
            }
        }

        if (expectedLength is > 0 && total != expectedLength)
        {
            throw new InvalidDataException(
                $"The model download was truncated ({total} of {expectedLength} bytes).");
        }
    }

    private static async Task VerifyHashAsync(
        string archivePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(archivePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(hash);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded model failed SHA-256 verification.");
        }
    }

    private static async Task ExtractAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("tar.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-xjf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destination);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start tar.exe to unpack the model.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort: cancellation still returns promptly if the extractor already exited.
            }
            throw;
        }
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"The model archive could not be unpacked: {error.Trim()}");
        }
    }
}
