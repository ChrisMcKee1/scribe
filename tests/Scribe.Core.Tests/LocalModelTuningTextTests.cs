using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// What each app's settings say on the AI cleanup page (<see cref="LocalModelTuningText"/>), and the dictionary page's count
/// with the whole vocabulary on: the words must match what Scribe does with the size and the vocabulary.
/// </summary>
public sealed class LocalModelTuningTextTests
{
    [Fact]
    public void The_size_list_starts_with_the_app_s_own_setting_and_names_each_size_in_tokens()
    {
        var sizes = LocalModelTuningText.ContextSizes("Ollama");

        Assert.Equal((0, "Ollama's setting"), sizes[0]);
        Assert.Equal(ContextBudget.OfferedSizes, sizes.Skip(1).Select(size => size.Tokens));
        Assert.Contains((32768, "32K (32,768 tokens)"), sizes);
        Assert.Equal("3,000 tokens", LocalModelTuningText.SizeLabel(3000));
    }

    [Fact]
    public void A_stored_size_the_list_does_not_offer_is_listed_as_it_is()
    {
        var sizes = LocalModelTuningText.ContextSizes("LM Studio", stored: 12000);

        Assert.Contains((12000, "12,000 tokens"), sizes);
        Assert.Equal(sizes.Skip(1).Select(size => size.Tokens).Order(), sizes.Skip(1).Select(size => size.Tokens));
        Assert.Equal(ContextBudget.OfferedSizes.Count + 1, LocalModelTuningText.ContextSizes("LM Studio", stored: 32768).Count);
    }

    [Fact]
    public void The_status_says_what_the_model_reads_and_whether_the_whole_vocabulary_fits()
    {
        Assert.Equal(
            "The model is reading up to 4,096 tokens. Your whole vocabulary needs about 8,308 tokens; at this size, about 1,480 of them fit beside a dictation.",
            LocalModelTuningText.ContextStatus("Ollama", inUse: 4096, asked: 32768, vocabularyTokens: 8308, vocabularyRoom: 1480));
        Assert.Equal(
            "Scribe asks for 32,768 tokens when the model loads. Your whole vocabulary needs about 8,308 tokens, which fits at this size.",
            LocalModelTuningText.ContextStatus("LM Studio", inUse: 0, asked: 32768, vocabularyTokens: 8308, vocabularyRoom: 29000));
        Assert.Equal(
            "Ollama sets the size when it loads the model. Your whole vocabulary needs about 8,308 tokens.",
            LocalModelTuningText.ContextStatus("Ollama", inUse: 0, asked: 0, vocabularyTokens: 8308));
        Assert.Equal(
            "Foundry Local sets the size when it loads the model. AI cleanup has no vocabulary to send.",
            LocalModelTuningText.ContextStatus("Foundry Local", inUse: 0, asked: 0, vocabularyTokens: 0));
        Assert.EndsWith(
            "; at this size, none of it fits beside a dictation.",
            LocalModelTuningText.ContextStatus("Ollama", inUse: 2048, asked: 0, vocabularyTokens: 500, vocabularyRoom: -40),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_room_Settings_quotes_is_the_room_a_readying_request_fits_the_vocabulary_into()
    {
        var options = CleanupHarness.Custom(LocalAiServer.OllamaAddress, "gemma4:e4b");

        Assert.Equal(
            ContextBudget.ReadyingVocabularyTokens(32768, TextCleanupService.BuildProbeSystemPrompt(options)),
            ContextBudget.VocabularyRoom(32768, options));
        Assert.True(ContextBudget.VocabularyRoom(4096, options) < 2000);
        Assert.True(ContextBudget.VocabularyRoom(32768, options) > 28000);
    }

    [Fact]
    public void Each_app_says_how_it_reads_the_whole_vocabulary()
    {
        // Ollama and LM Studio cache what every request starts with, so they read it once a load; Foundry Local caches
        // nothing (measured on 2.1.0: cached tokens 0, and the time grew with the prompt on every request).
        Assert.Contains("each time it loads", LocalModelTuningText.AppWholeVocabularyHint, StringComparison.Ordinal);
        Assert.Contains("for every dictation", LocalModelTuningText.FoundryWholeVocabularyHint, StringComparison.Ordinal);

        // The 0.5.3 benchmark: a 2B model got fewer word pack terms right with the whole list.
        foreach (var hint in new[] { LocalModelTuningText.AppWholeVocabularyHint, LocalModelTuningText.FoundryWholeVocabularyHint })
        {
            Assert.Contains("A long list can confuse a small model", hint, StringComparison.Ordinal);
            Assert.Contains("the word packs you let AI cleanup use", hint, StringComparison.Ordinal);
        }

        Assert.Contains("reloads the model", LocalModelTuningText.OllamaContextSizeHint, StringComparison.Ordinal);
        Assert.Contains("Context length in Ollama's settings", LocalModelTuningText.OllamaContextSizeHint, StringComparison.Ordinal);
        Assert.Contains("frees it after the idle time", LocalModelTuningText.LmStudioContextSizeHint, StringComparison.Ordinal);
        Assert.Contains("keeps its own size", LocalModelTuningText.LmStudioContextSizeHint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_texts_are_free_of_em_and_en_dashes()
    {
        string[] texts =
        [
            LocalModelTuningText.TuningSummary,
            LocalModelTuningText.WholeVocabularyTitle,
            LocalModelTuningText.AppWholeVocabularyHint,
            LocalModelTuningText.FoundryWholeVocabularyHint,
            LocalModelTuningText.ContextSizeTitle,
            LocalModelTuningText.OllamaContextSizeHint,
            LocalModelTuningText.LmStudioContextSizeHint,
            LocalModelTuningText.ContextStatus("Ollama", 4096, 32768, 9000, 1500),
        ];

        Assert.All(texts, text => Assert.DoesNotContain(text, ch => ch is '\u2014' or '\u2013'));
    }

    [Fact]
    public void With_the_whole_vocabulary_on_the_dictionary_page_says_all_of_it_goes_when_it_fits()
    {
        var rows = Enumerable.Range(0, 200)
            .Select(i => new DictionaryEntryBuilder.Row(0, $"spoken term {i}", $"Term{i}", true, true))
            .ToList();
        GlossaryHint.Input Input(bool whole, string endpoint) => new(
            rows, [], AiCleanupOn: true, PostProcessingOn: true, CleanupProvider.OpenAiCompatible, CleanupPromptStyle.Auto,
            CustomEndpoint: endpoint, SendWholeVocabulary: whole);

        var tokens = GlossaryHint.WholeVocabularyTokens(Input(true, LocalAiServer.OllamaAddress));
        var whole = GlossaryHint.Describe(Input(true, LocalAiServer.OllamaAddress));

        Assert.Contains(
            $"The AI model on this PC receives all 200 of these words with each cleanup request when they fit in its context " +
            $"with the dictation, about {tokens:N0} tokens.",
            whole,
            StringComparison.Ordinal);
        Assert.DoesNotContain("holds up to 80", whole, StringComparison.Ordinal);
        Assert.Equal(
            CleanupPrompt.GlossaryHeaderTokens + CleanupPrompt.Tokens(CleanupPrompt.GlossaryLines(DictionaryEntryBuilder.Build(rows).Entries.OrderBy(e => e.Pattern, StringComparer.Ordinal))),
            tokens);

        // Off, or a service elsewhere, is what every release since 0.5.2 said.
        Assert.Contains("whichever of these 200 words a dictation appears to mention", GlossaryHint.Describe(Input(false, LocalAiServer.OllamaAddress)), StringComparison.Ordinal);
        Assert.Contains("Your AI service receives whichever", GlossaryHint.Describe(Input(true, "https://openrouter.ai/api/v1")), StringComparison.Ordinal);
    }
}
