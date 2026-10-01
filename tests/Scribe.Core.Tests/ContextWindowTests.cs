using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Fitting a request to a model on this PC into its context: the estimate of what text costs, the room a request leaves
/// for vocabulary once the dictated text and its answer have theirs, and which vocabulary goes in it. The dictated text
/// always comes first; with the whole vocabulary on, all of it goes when it fits, and otherwise the terms a dictation
/// mentions keep their room and the rest fills what is left in an order that stays the same from one dictation to the
/// next, so Ollama and LM Studio find it already read.
/// </summary>
public sealed class ContextWindowTests
{
    [Fact]
    public void The_estimate_counts_prose_and_vocabulary_at_their_own_rates_and_every_other_character_as_a_token()
    {
        Assert.Equal(0, TokenEstimate.Prose(string.Empty));
        Assert.Equal((int)Math.Ceiling(360 / TokenEstimate.ProseCharsPerToken), TokenEstimate.Prose(new string('a', 360)));
        Assert.Equal((int)Math.Ceiling(260 / TokenEstimate.VocabularyCharsPerToken), TokenEstimate.Vocabulary(new string('a', 260)));
        Assert.Equal(TokenEstimate.Prose("abc") + TokenEstimate.ShortTextAllowance, TokenEstimate.Transcript("abc"));

        // Text in a script these tokenizers split finely is never undercounted: each character outside ASCII is a token.
        Assert.Equal(10, TokenEstimate.Prose("日本語のテキストです。".AsSpan(0, 10)));
        Assert.Equal(1 + 3, TokenEstimate.Vocabulary("ab日本語"));
    }

    [Fact]
    public void The_estimate_is_above_what_the_tokenizers_measured()
    {
        // Measured on Ollama 0.35.0: Scribe's short instructions with its writing style took 4.37 characters a token, the
        // benchmark dictations 4.18 on average and 2.99 at worst, a vocabulary of 1,367 terms 2.87 (Gemma) to 3.29.
        Assert.True(TokenEstimate.ProseCharsPerToken < 4.18);
        Assert.True(TokenEstimate.VocabularyCharsPerToken < 2.87);
        var shortDictation = "<transcript>\nhi\n</transcript>";
        Assert.True(TokenEstimate.Transcript("hi") >= shortDictation.Length / 2.99);

        var instructions = TextCleanupService.BuildProbeSystemPrompt(CleanupHarness.Custom("http://localhost:11434/v1"));
        Assert.True(TokenEstimate.Prose(instructions) >= instructions.Length / 4.37);
    }

    [Fact]
    public void The_dictated_text_and_its_whole_answer_take_their_room_before_the_vocabulary()
    {
        const string Instructions = "Rewrite the dictation.";
        var shortText = "send the report to sarah";
        var longText = string.Join(' ', Enumerable.Repeat("we need to ship the build by thursday", 60));

        var forShort = ContextBudget.VocabularyTokens(4096, Instructions, shortText, outputCeiling: 256);
        var forLong = ContextBudget.VocabularyTokens(4096, Instructions, longText, outputCeiling: 256);

        Assert.True(forShort > forLong);
        var transcript = TokenEstimate.Transcript(longText);
        Assert.Equal(
            4096 - ContextBudget.ChatTemplateTokens - TokenEstimate.Prose(Instructions) - transcript - 256 -
                ContextBudget.Margin(4096),
            forLong);

        // The whole output ceiling the request declares is reserved: the request and its longest allowed answer fit together.
        Assert.Equal(forLong - 100, ContextBudget.VocabularyTokens(4096, Instructions, longText, outputCeiling: 356));
        Assert.Equal(forLong + 256, ContextBudget.VocabularyTokens(4096, Instructions, longText, outputCeiling: 0));
    }

    [Fact]
    public void Text_that_does_not_fit_leaves_no_room_at_all()
    {
        var instructions = TextCleanupService.BuildProbeSystemPrompt(CleanupHarness.Custom("http://localhost:11434/v1"));
        var text = new string('a', 20_000);

        Assert.True(ContextBudget.VocabularyTokens(4096, instructions, text, outputCeiling: 4096) < 0);
        Assert.False(ContextBudget.TextFits(4096, instructions, text, outputCeiling: 4096));
        Assert.True(ContextBudget.TextFits(32768, instructions, text, outputCeiling: 4096));
    }

    [Fact]
    public void A_readying_request_leaves_room_for_a_typical_dictation_and_its_answer()
    {
        const string Instructions = "Rewrite the dictation.";
        var typical = new string('a', (int)(ContextBudget.ReadyingTranscriptTokens * TokenEstimate.ProseCharsPerToken));
        var readying = ContextBudget.ReadyingVocabularyTokens(8192, Instructions);
        var dictation = ContextBudget.VocabularyTokens(8192, Instructions, typical, ContextBudget.ReadyingOutputTokens);

        Assert.InRange(readying - dictation, -32, 32);

        // The answer it leaves room for covers what a dictation of that length declares.
        var words = string.Join(' ', Enumerable.Repeat("word", 1800 / 5));
        Assert.True(TextCleanupService.EstimateMaxTokens(words, CleanupProvider.OpenAiCompatible) <= ContextBudget.ReadyingOutputTokens);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(100, ContextBudget.MinimumSize)]
    [InlineData(32768, 32768)]
    [InlineData(int.MaxValue, ContextBudget.MaximumSize)]
    public void A_size_is_the_app_s_own_setting_or_within_bounds(int stored, int expected) =>
        Assert.Equal(expected, ContextBudget.Sanitize(stored));

    [Fact]
    public void The_glossary_lines_are_the_ones_the_glossary_renders_in_its_order()
    {
        var entries = Entries(60);

        var lines = CleanupPrompt.GlossaryLines(entries);
        var rendered = CleanupPrompt.RenderGlossary(lines);

        Assert.Equal(CleanupPrompt.BuildGlossary(entries), rendered);
        Assert.Equal(CleanupPrompt.CountGlossary(entries).Eligible, lines.Count);
        Assert.All(lines, line => Assert.Equal(TokenEstimate.Vocabulary(line.Text) + 1, line.Tokens));
        Assert.Equal(string.Empty, CleanupPrompt.RenderGlossary([]));
    }

    [Fact]
    public void The_glossary_lines_leave_out_what_the_glossary_leaves_out()
    {
        DictionaryEntry[] entries =
        [
            DictionaryEntry.New("k eight s", "Kubernetes"),
            DictionaryEntry.New("K EIGHT S", "kubernetes"),
            DictionaryEntry.New("my sig", "Best,\nChris"),
            DictionaryEntry.New("off", "Off") with { Enabled = false },
            DictionaryEntry.New("az", "Azure"),
        ];

        var lines = CleanupPrompt.GlossaryLines(entries);

        Assert.Equal(["- Kubernetes (transcribed as \"k eight s\")", "- Azure (transcribed as \"az\")"], lines.Select(line => line.Text));
    }

    [Fact]
    public void Without_the_whole_vocabulary_a_request_carries_the_mentioned_terms_that_fit()
    {
        var all = Lines(100);
        IReadOnlyList<GlossaryLineInfo> mentioned = [all[3], all[50], all[90]];
        var room = CleanupPrompt.GlossaryHeaderTokens + all[3].Tokens + all[50].Tokens;

        Assert.Equal(mentioned, CleanupPrompt.FitGlossary(all, mentioned, everything: false, tokenBudget: 100_000, maxTerms: 80));
        Assert.Equal([all[3], all[50]], CleanupPrompt.FitGlossary(all, mentioned, everything: false, room, maxTerms: 80));
        Assert.Equal([all[3]], CleanupPrompt.FitGlossary(all, mentioned, everything: false, tokenBudget: 100_000, maxTerms: 1));

        // Rendered exactly as every release since 0.5.2 rendered the mentioned terms.
        Assert.Equal(
            CleanupPrompt.BuildGlossary(Entries(100).Where((_, i) => i is 3 or 50 or 90)),
            CleanupPrompt.RenderGlossary(CleanupPrompt.FitGlossary(all, mentioned, everything: false, tokenBudget: 100_000, maxTerms: 80)));
    }

    [Fact]
    public void With_the_whole_vocabulary_all_of_it_goes_when_it_fits()
    {
        var all = Lines(100);

        var fitted = CleanupPrompt.FitGlossary(all, [all[7]], everything: true, tokenBudget: 1_000_000, maxTerms: int.MaxValue);

        Assert.Same(all, fitted);
    }

    [Fact]
    public void When_it_does_not_fit_the_mentioned_terms_keep_their_room_and_the_rest_runs_from_the_start()
    {
        var all = Lines(200);
        IReadOnlyList<GlossaryLineInfo> mentioned = [all[2], all[150], all[180]];
        var budget = CleanupPrompt.GlossaryHeaderTokens + (int)CleanupPrompt.Tokens(all.Take(40).ToList()) + all[150].Tokens + all[180].Tokens;

        var fitted = CleanupPrompt.FitGlossary(all, mentioned, everything: true, budget, maxTerms: int.MaxValue);

        // The run from the start holds the mentioned term it reaches without paying for it again, then the other two follow.
        var run = fitted.Take(fitted.Count - 2).ToList();
        Assert.Equal(all.Take(run.Count), run);
        Assert.True(run.Count >= 40);
        Assert.Equal([all[150], all[180]], fitted.Skip(run.Count));
        Assert.True(CleanupPrompt.GlossaryHeaderTokens + CleanupPrompt.Tokens(fitted) <= budget);
        Assert.Equal(fitted.Count, fitted.Select(line => line.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(TokenEstimate.Vocabulary(CleanupPrompt.RenderGlossary(fitted)) <= budget);
    }

    [Fact]
    public void Two_dictations_share_the_start_of_what_they_send_so_the_app_reads_it_once()
    {
        var all = Lines(500);
        var budget = CleanupPrompt.GlossaryHeaderTokens + (int)CleanupPrompt.Tokens(all.Take(200).ToList());

        var first = CleanupPrompt.FitGlossary(all, [all[300]], everything: true, budget, int.MaxValue);
        var second = CleanupPrompt.FitGlossary(all, [all[10], all[400], all[450]], everything: true, budget, int.MaxValue);

        var shared = Math.Min(first.Count, second.Count) - 3;
        Assert.True(shared > 150);
        Assert.Equal(first.Take(shared), second.Take(shared));
        Assert.Equal(all.Take(shared), first.Take(shared));
    }

    [Fact]
    public void Mentioned_terms_that_alone_overflow_are_cut_and_nothing_else_goes()
    {
        var all = Lines(50);
        IReadOnlyList<GlossaryLineInfo> mentioned = [all[10], all[20], all[30]];
        var budget = CleanupPrompt.GlossaryHeaderTokens + all[10].Tokens;

        Assert.Equal([all[10]], CleanupPrompt.FitGlossary(all, mentioned, everything: true, budget, int.MaxValue));
        Assert.Empty(CleanupPrompt.FitGlossary(all, mentioned, everything: true, CleanupPrompt.GlossaryHeaderTokens, int.MaxValue));
        Assert.Empty(CleanupPrompt.FitGlossary(all, mentioned, everything: true, tokenBudget: -500, int.MaxValue));
    }

    [Fact]
    public void A_term_cap_bounds_the_run_and_the_mentioned_terms_together()
    {
        var all = Lines(100);
        IReadOnlyList<GlossaryLineInfo> mentioned = [all[60], all[70]];

        var fitted = CleanupPrompt.FitGlossary(all, mentioned, everything: true, tokenBudget: 1_000_000, maxTerms: 10);

        Assert.Equal(10, fitted.Count);
        Assert.Equal(all.Take(8), fitted.Take(8));
        Assert.Equal([all[60], all[70]], fitted.Skip(8));
    }

    [Fact]
    public void A_vocabulary_fits_its_glossary_for_a_dictation_a_readying_request_and_each_mode()
    {
        var entries = Entries(300);
        var vocabulary = new CleanupVocabulary(entries, AiVocabularyScope.None);
        var dictation = "please update spoken phrase 7 and spoken phrase 250 today";

        // Without the whole vocabulary, what every release since 0.5.2 sent when the context has room.
        var mentioned = vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, everything: false, dictation, 100_000, 80);
        Assert.Equal(CleanupPrompt.BuildGlossary(VocabularyMentions.Select(entries, dictation), 80), mentioned);

        var whole = vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, everything: true, dictation, 1_000_000, int.MaxValue);
        Assert.Equal(CleanupPrompt.BuildGlossary(entries), whole);
        Assert.Same(whole, vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, everything: true, "other words", 1_000_000, int.MaxValue));

        // A readying request has no dictation: the leading run when the whole vocabulary is on, nothing otherwise.
        Assert.Null(vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, everything: false, dictation: null, 100_000, 80));
        var readying = vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, everything: true, dictation: null, 400, int.MaxValue);
        Assert.NotNull(readying);
        Assert.StartsWith(readying, whole!, StringComparison.Ordinal);

        Assert.Null(vocabulary.GlossaryFor(CleanupVocabularyMode.None, everything: true, dictation, 1_000_000, int.MaxValue));
        Assert.NotNull(vocabulary.GlossaryFor(CleanupVocabularyMode.All, everything: false, dictation, 400, 80));
        Assert.Null(vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, everything: true, dictation, 10, int.MaxValue));
        Assert.Equal(
            CleanupPrompt.GlossaryHeaderTokens + CleanupPrompt.Tokens(CleanupPrompt.GlossaryLines(Entries(300))),
            vocabulary.WholeGlossaryTokens);
    }

    [Fact]
    public void Each_app_keeps_its_own_tuning()
    {
        var settings = new AppSettings
        {
            AiCleanupOllamaContextTokens = 32768,
            AiCleanupOllamaSendWholeVocabulary = true,
            AiCleanupLmStudioContextTokens = 16384,
            AiCleanupLmStudioSendWholeVocabulary = false,
            AiCleanupFoundryLocalSendWholeVocabulary = true,
        };

        var ollama = LocalModelTuning.Apply(CleanupHarness.Custom(LocalAiServer.OllamaAddress), settings);
        Assert.Equal(32768, ollama.LocalContextTokens);
        Assert.True(ollama.SendWholeVocabulary);

        var lmStudio = LocalModelTuning.Apply(CleanupHarness.Custom(LocalAiServer.LmStudioAddress), settings);
        Assert.Equal(16384, lmStudio.LocalContextTokens);
        Assert.False(lmStudio.SendWholeVocabulary);

        var foundry = LocalModelTuning.Apply(CleanupHarness.FoundryOn(), settings);
        Assert.Null(foundry.LocalContextTokens);
        Assert.True(foundry.SendWholeVocabulary);

        // Another AI service, even one on this PC, gets none of it.
        foreach (var other in new[] { "http://localhost:8080/v1", "https://openrouter.ai/api/v1" })
        {
            var custom = LocalModelTuning.Apply(CleanupHarness.Custom(other), settings);
            Assert.Null(custom.LocalContextTokens);
            Assert.False(custom.SendWholeVocabulary);
        }

        // Ollama's setting (0) leaves the size to Ollama.
        Assert.Null(LocalModelTuning.Apply(CleanupHarness.Custom(LocalAiServer.OllamaAddress), new AppSettings()).LocalContextTokens);
    }

    [Fact]
    public void The_whole_vocabulary_is_a_prompt_field_and_the_size_is_not()
    {
        var options = CleanupHarness.Custom(LocalAiServer.OllamaAddress);

        Assert.True(options.MatchesIgnoringPrompt(options with { SendWholeVocabulary = true }));
        Assert.False(options.MatchesIgnoringPrompt(options with { LocalContextTokens = 32768 }));
    }

    [Fact]
    public void Every_install_starts_with_the_apps_own_settings_and_without_the_whole_vocabulary()
    {
        foreach (var settings in new[] { new AppSettings(), AppSettings.CreateDefault(), AppSettings.CreateForExistingInstall() })
        {
            Assert.Equal(0, settings.AiCleanupOllamaContextTokens);
            Assert.Equal(0, settings.AiCleanupLmStudioContextTokens);
            Assert.False(settings.AiCleanupOllamaSendWholeVocabulary);
            Assert.False(settings.AiCleanupLmStudioSendWholeVocabulary);
            Assert.False(settings.AiCleanupFoundryLocalSendWholeVocabulary);
        }
    }

    private static List<DictionaryEntry> Entries(int count) =>
        [.. Enumerable.Range(0, count).Select(i => DictionaryEntry.New($"spoken phrase {i}", $"Phrase{i}"))];

    private static List<GlossaryLineInfo> Lines(int count) => CleanupPrompt.GlossaryLines(Entries(count));
}
