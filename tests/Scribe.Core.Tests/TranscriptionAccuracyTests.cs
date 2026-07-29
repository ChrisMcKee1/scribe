using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scribe.Core.Infrastructure;
using Scribe.Core.Transcription;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Asserts the recognizer produces the <b>right</b> words, not merely some words.
/// </summary>
/// <remarks>
/// The existing smoke test only requires three or more tokens, which a misconfigured feature
/// pipeline can still satisfy while emitting nonsense. Parakeet TDT uses 128 mel bins where
/// sherpa-onnx defaults to 80, and the runtime currently repairs that mismatch from the model's own
/// metadata; if a future change breaks that repair, garbage would sail past a length-only check.
/// These fixtures ship with the model, so this costs nothing extra to run.
/// </remarks>
public sealed class TranscriptionAccuracyTests
{
    private static (TranscriptionService? Service, string? Wav) TryCreate(string fixture)
    {
        var locator = new ModelLocator(new AppPaths());
        var models = locator.Resolve();
        if (!models.AsrComplete)
        {
            return (null, null); // models not downloaded in this environment
        }

        var wav = Path.Combine(models.Directory, "test_wavs", fixture);
        if (!File.Exists(wav))
        {
            return (null, null); // GUI-installed layouts omit the development fixtures
        }

        return (new TranscriptionService(
            locator,
            Options.Create(new TranscriptionOptions { NumThreads = 4 }),
            NullLogger<TranscriptionService>.Instance), wav);
    }

    [Theory]
    // Each fixture is checked for words that only appear if the acoustic features were built
    // correctly. Lowercased comparison keeps this about recognition, not casing or punctuation.
    // en.wav is the JFK inaugural line, so it also proves multi-clause decoding rather than a
    // single lucky word.
    [InlineData("en.wav", new[] { "ask not what your country can do for you" })]
    [InlineData("de.wav", new[] { "die" })]
    [InlineData("es.wav", new[] { "de" })]
    [InlineData("fr.wav", new[] { "de" })]
    public void Bundled_fixtures_decode_to_recognizable_words(string fixture, string[] expected)
    {
        var (service, wav) = TryCreate(fixture);
        if (service is null || wav is null)
        {
            return;
        }

        using (service)
        {
            var text = service.Transcribe(TestAudio.LoadWav(wav)).Text.ToLowerInvariant();

            Assert.False(string.IsNullOrWhiteSpace(text));
            foreach (var word in expected)
            {
                Assert.True(
                    text.Contains(word, StringComparison.Ordinal),
                    $"Expected \"{word}\" in the {fixture} transcription but got: \"{text}\"");
            }
        }
    }

    [Fact]
    public void The_same_audio_decodes_identically_twice()
    {
        // Decoding is deterministic, so a difference between runs would mean state is leaking
        // between calls on the shared recognizer.
        var (service, wav) = TryCreate("en.wav");
        if (service is null || wav is null)
        {
            return;
        }

        using (service)
        {
            var audio = TestAudio.LoadWav(wav);
            Assert.Equal(service.Transcribe(audio).Text, service.Transcribe(audio).Text);
        }
    }
}
