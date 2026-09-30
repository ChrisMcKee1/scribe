using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The vocabulary a dictation appears to mention (<see cref="VocabularyMentions"/>), and the requests that carry only that
/// under <see cref="CleanupVocabularyMode.Mentioned"/>. The dictations are what speech recognition made of real names in
/// the local model benchmark's cases.
/// </summary>
public sealed class VocabularyMentionsTests
{
    private static readonly DictionaryEntry[] Vocabulary =
    [
        DictionaryEntry.New("o llama", "Ollama"),
        DictionaryEntry.New("qwen three thirty two b", "Qwen3-32B"),
        DictionaryEntry.New("phi four mini", "Phi-4-mini"),
        DictionaryEntry.New("gpt five six terra", "GPT-5.6-Terra"),
        DictionaryEntry.New("gpt five four mini", "GPT-5.4-mini"),
        DictionaryEntry.New("text embedding three large", "text-embedding-3-large"),
        DictionaryEntry.New("cube control", "kubectl"),
        DictionaryEntry.New("claude opus four point eight", "Claude Opus 4.8"),
        DictionaryEntry.New("kubernetes", "Kubernetes"),
        DictionaryEntry.New("large language model", "Large Language Model"),
    ];

    [Theory]
    [InlineData("we tried it locally through o llama", "Ollama")]
    [InlineData("we tried quen 332B locally through Alama", "Ollama")]
    [InlineData("we tried quen 332B locally through Alama", "Qwen3-32B")]
    [InlineData("a LoRa run on Fi4 Mini with 8,000 examples", "Phi-4-mini")]
    [InlineData("I'm running a test for GPT 56Tera to see how it does", "GPT-5.6-Terra")]
    [InlineData("the embeddings come from text embedding 3 large", "text-embedding-3-large")]
    [InlineData("run cube control apply on the cluster", "kubectl")]
    [InlineData("comparing Claude Opus 11 against the others", "Claude Opus 4.8")]
    [InlineData("a large language mode that runs on the laptop", "Large Language Model")]
    public void A_name_speech_recognition_mangled_still_counts_as_mentioned(string dictation, string written)
    {
        var selected = VocabularyMentions.Select(Vocabulary, dictation).Select(e => e.Replacement).ToList();

        Assert.Contains(written, selected);
    }

    [Fact]
    public void A_name_counts_only_when_every_word_of_it_does()
    {
        // "gpt" alone does not bring in the mini model, whose other word was never said.
        var selected = VocabularyMentions.Select(Vocabulary, "the gpt five six terra results were fine")
            .Select(e => e.Replacement).ToList();

        Assert.Contains("GPT-5.6-Terra", selected);
        Assert.DoesNotContain("GPT-5.4-mini", selected);
    }

    [Theory]
    [InlineData("let's get lunch on thursday and talk about the budget")]
    [InlineData("please send the quarterly report to Sarah by Friday")]
    public void A_dictation_that_names_none_of_them_carries_none(string dictation) =>
        Assert.Empty(VocabularyMentions.Select(Vocabulary, dictation));

    [Fact]
    public void Common_words_in_a_name_say_nothing_on_their_own()
    {
        // "large" and "model" are ordinary words; the name needs "language" too.
        Assert.DoesNotContain(
            VocabularyMentions.Select(Vocabulary, "the large model was slow"),
            e => e.Replacement == "Large Language Model");
    }

    [Fact]
    public void Disabled_entries_are_never_selected_and_the_order_is_kept()
    {
        var entries = new[]
        {
            DictionaryEntry.New("kubernetes", "Kubernetes"),
            DictionaryEntry.New("o llama", "Ollama") with { Enabled = false },
            DictionaryEntry.New("cube control", "kubectl"),
        };

        var selected = VocabularyMentions.Select(entries, "kubernetes and cube control through o llama");

        Assert.Equal(["Kubernetes", "kubectl"], selected.Select(e => e.Replacement));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("... !!!")]
    public void No_words_means_no_vocabulary(string? dictation) =>
        Assert.Empty(VocabularyMentions.Select(Vocabulary, dictation));

    [Theory]
    [InlineData("ollama", "alama", true)]
    [InlineData("qwen", "quen", true)]
    [InlineData("terra", "tera", true)]
    [InlineData("ollama", "llama", false)]
    [InlineData("kubernetes", "cabernet", false)]
    public void Sound_keys_match_what_sounds_alike(string a, string b, bool same) =>
        Assert.Equal(same, VocabularyMentions.SoundKey(a) == VocabularyMentions.SoundKey(b));

    [Fact]
    public void Parts_split_letters_from_digits()
    {
        Assert.Equal(["qwen", "3", "14", "b"], VocabularyMentions.Parts("Qwen3-14B"));
        Assert.Equal(["gpt", "5", "6", "terra"], VocabularyMentions.Parts("GPT-5.6-Terra"));
    }

    [Fact]
    public void A_vocabulary_of_thousands_is_searched_quickly()
    {
        var entries = Enumerable.Range(0, 5000)
            .Select(i => DictionaryEntry.New($"product {i} name", $"Product{i}Name"))
            .ToList();
        var dictation = string.Join(' ', Enumerable.Repeat("so we shipped the release on thursday and it went well", 40));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var selected = VocabularyMentions.Select(entries, dictation);
        watch.Stop();

        Assert.Empty(selected);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Each_mode_renders_its_own_glossary()
    {
        var vocabulary = new CleanupVocabulary(Vocabulary, AiVocabularyScope.None);
        const string dictation = "we tried it locally through o llama";

        Assert.Equal(vocabulary.GlossaryFor(80), vocabulary.GlossaryFor(80, CleanupVocabularyMode.All, dictation));
        Assert.Null(vocabulary.GlossaryFor(80, CleanupVocabularyMode.None, dictation));
        Assert.Null(vocabulary.GlossaryFor(80, CleanupVocabularyMode.Mentioned, dictation: null));

        var mentioned = vocabulary.GlossaryFor(80, CleanupVocabularyMode.Mentioned, dictation);
        Assert.NotNull(mentioned);
        Assert.Contains("Ollama", mentioned, StringComparison.Ordinal);
        Assert.DoesNotContain("Kubernetes", mentioned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dictation_under_the_mentioned_vocabulary_sends_only_what_it_mentions_and_the_readying_request_none()
    {
        var bodies = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct);
            lock (bodies)
            {
                bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
            }

            return ScriptedHttpHandler.ChatCompletion("We tried it locally through Ollama.");
        });

        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(
            CleanupHarness.Custom("http://127.0.0.1:11434/v1", "gemma4:e2b") with { VocabularyMode = CleanupVocabularyMode.Mentioned });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var vocabulary = new CleanupVocabulary(Vocabulary, AiVocabularyScope.None);

        harness.Service.ForgetLastModelAnswerForTesting();
        harness.Service.Admit(vocabulary).Prewarm();
        await harness.Service.WaitForPrewarmForTesting();
        var readying = SystemMessage(Last(bodies));
        Assert.DoesNotContain("Preferred vocabulary", readying, StringComparison.Ordinal);

        var result = await harness.Service.Admit(vocabulary).CleanAsync("we tried it locally through o llama");
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        var sent = SystemMessage(Last(bodies));
        Assert.Contains("Ollama", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Kubernetes", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("kubectl", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("text-embedding-3-large", sent, StringComparison.Ordinal);

        // The instructions before the vocabulary are the same, so a server that cached them from the readying request
        // only reads the vocabulary and the dictation.
        Assert.StartsWith(readying, sent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vocabulary_mode_is_a_prompt_field()
    {
        var options = CleanupHarness.Custom("http://127.0.0.1:11434/v1", "gemma4:e2b");

        Assert.True(options.MatchesIgnoringPrompt(options with { VocabularyMode = CleanupVocabularyMode.Mentioned }));
    }

    private static JsonElement Last(List<JsonElement> bodies)
    {
        lock (bodies)
        {
            return bodies[^1];
        }
    }

    private static string SystemMessage(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
}
