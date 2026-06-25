using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.Core.Tests;

/// <summary>
/// Offline engine smoke test (P0-2): decodes a bundled fixture through the real sherpa-onnx
/// Parakeet recognizer and asserts non-empty text. Proves fully on-device transcription.
/// Skips when the model is not present (e.g. CI without <c>Download-Models.ps1</c>).
/// </summary>
public sealed class TranscriptionServiceTests
{
    [Fact]
    public void Transcribe_EnglishFixture_ReturnsText()
    {
        var locator = new ModelLocator(new AppPaths());
        var models = locator.Resolve();
        if (!models.AsrComplete)
            return; // Models not downloaded in this environment; nothing to verify.

        var wav = Path.Combine(models.Directory, "test_wavs", "en.wav");
        Assert.True(File.Exists(wav), $"Missing fixture: {wav}");

        using var service = new TranscriptionService(
            locator,
            Options.Create(new TranscriptionOptions { NumThreads = 4 }),
            NullLogger<TranscriptionService>.Instance);

        var audio = TestAudio.LoadWav(wav);
        var result = service.Transcribe(audio);

        Assert.True(service.IsReady);
        Assert.False(result.IsEmpty);
        Assert.True(
            result.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3,
            $"Expected a multi-word transcription but got: \"{result.Text}\"");
        Assert.True(result.AudioDuration > TimeSpan.Zero);
    }

    [Fact]
    public void Transcribe_EmptyAudio_ReturnsEmptyWithoutLoadingModel()
    {
        var locator = new ModelLocator(new AppPaths());
        using var service = new TranscriptionService(
            locator,
            Options.Create(new TranscriptionOptions()),
            NullLogger<TranscriptionService>.Instance);

        var result = service.Transcribe(CapturedAudio.Empty);

        Assert.True(result.IsEmpty);
        Assert.False(service.IsReady); // empty input must short-circuit before model load
    }
}
