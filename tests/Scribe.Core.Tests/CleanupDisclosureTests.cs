using System.Globalization;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// The AI cleanup disclosure is a promise, so it is held to the code. The AI cleanup page said remote
/// providers get "relevant dictionary terms", the service-principal guide said cleanup sends "the
/// transcribed text only", and in fact every request carries every enabled dictionary and library
/// term. These tests pin the wording Settings shows to the limits the code enforces, pin the privacy
/// policy's facts, and keep the retired claims from coming back in any user-facing document.
/// </summary>
public sealed class CleanupDisclosureTests
{
    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    [Fact]
    public void The_ai_cleanup_page_says_what_every_request_carries_with_the_limits_the_code_enforces()
    {
        var text = CleanupDisclosure.WhatCleanupSends;

        Assert.Contains("Foundry Local runs cleanup on this PC", text, StringComparison.Ordinal);
        foreach (var provider in new[] { "Microsoft Foundry", "GitHub Copilot", "any other AI service you set up" })
        {
            Assert.Contains(provider, text, StringComparison.Ordinal);
        }

        Assert.Contains("with every cleanup request", text, StringComparison.Ordinal);
        Assert.Contains("the text Scribe recognized for that dictation", text, StringComparison.Ordinal);
        Assert.Contains("writing style", text, StringComparison.Ordinal);
        Assert.Contains("your dictionary plus the word packs you let AI cleanup use", text, StringComparison.Ordinal);
        Assert.Contains(
            $"up to {N(CleanupPrompt.MaxGlossaryTermsCloud)} words or phrases and {N(CleanupPrompt.MaxGlossaryChars)} characters",
            text, StringComparison.Ordinal);
        Assert.Contains($"{N(CleanupPrompt.MaxGlossaryTermsLocal)} words or phrases with the short instructions", text, StringComparison.Ordinal);

        // The limits count entries, and an entry can be a phrase: a limit given in bare words would understate what goes.
        Assert.DoesNotContain($"{N(CleanupPrompt.MaxGlossaryTermsCloud)} words and", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{N(CleanupPrompt.MaxGlossaryTermsLocal)} words with", text, StringComparison.Ordinal);

        // Each request carries the vocabulary its dictation appears to mention (CleanupVocabularyMode.Mentioned), including
        // words heard slightly differently; the card must not promise more precision than the matcher has, nor say that
        // everything goes.
        Assert.Contains("that the dictation appears to mention, including ones Scribe heard slightly differently", text, StringComparison.Ordinal);
        Assert.DoesNotContain("whether or not the dictation mentions", text, StringComparison.Ordinal);
        // About dictionary and word pack words, never the dictation: a dictation that spans lines is sent.
        Assert.Contains(
            "A word from your dictionary or a word pack is not vocabulary, and is not sent, when what Scribe writes " +
            $"for it spans more than one line or runs past {N(CleanupPrompt.MaxGlossaryTermChars)} characters, such as a signature.",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("Anything Scribe writes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("relevant", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("only", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_client_request_headers_are_disclosed_in_settings_and_the_privacy_policy()
    {
        var text = CleanupDisclosure.AiClientRequestMetadata;
        Assert.Contains(text, CleanupDisclosure.WhatCleanupSends, StringComparison.Ordinal);
        foreach (var field in new[] { "language and version", "operating system", "processor architecture", "runtime name and version" })
        {
            Assert.Contains(field, text, StringComparison.Ordinal);
        }

        Assert.Contains("same AI service", text, StringComparison.Ordinal);
        var privacy = File.ReadAllText(Path.Combine(RepositoryRoot(), "PRIVACY.md"));
        Assert.Contains("six `X-Stainless-*`", privacy, StringComparison.Ordinal);
        Assert.Contains("not to a separate telemetry service", privacy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_openai_client_sends_the_six_disclosed_metadata_headers_to_the_request_service()
    {
        var requests = new List<Dictionary<string, string>>();
        var http = new ScriptedHttpHandler((request, _) =>
        {
            requests.Add(request.Headers
                .Where(header => header.Key.StartsWith("X-Stainless-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase));
            return Task.FromResult(ScriptedHttpHandler.ChatCompletion("So we ship on Friday."));
        });
        await using var harness = new CleanupHarness(http: http);
        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "test-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(CleanupOutcome.Cleaned,
            (await harness.Service.CleanAsync("um so we ship on friday", CancellationToken.None)).Outcome);

        Assert.True(requests.Count >= 2);
        Assert.All(requests, headers =>
        {
            foreach (var field in new[]
                     {
                         "X-Stainless-Lang", "X-Stainless-Package-Version", "X-Stainless-OS",
                         "X-Stainless-Arch", "X-Stainless-Runtime", "X-Stainless-Runtime-Version",
                     })
            {
                Assert.True(headers.TryGetValue(field, out var value), field);
                Assert.False(string.IsNullOrWhiteSpace(value), field);
            }
        });
    }

    [Fact]
    public void The_connection_check_is_disclosed_and_says_it_carries_no_vocabulary()
    {
        var text = CleanupDisclosure.WhatCleanupNeverSends;

        Assert.Contains("Each time cleanup connects", text, StringComparison.Ordinal);
        Assert.Contains("test request", text, StringComparison.Ordinal);
        Assert.Contains("none of your vocabulary", text, StringComparison.Ordinal);
        Assert.Contains("snippet templates", text, StringComparison.Ordinal);
        Assert.Contains("audio never leaves this device", text, StringComparison.Ordinal);
        Assert.Contains("GitHub Copilot sends all of this to GitHub", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CleanupProvider.FoundryLocal, "Your text, writing style and vocabulary stay on this PC. Audio never leaves it.")]
    [InlineData(CleanupProvider.AzureFoundry, "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack words it mentions to your Microsoft Foundry deployment. Audio never leaves this PC.")]
    [InlineData(CleanupProvider.OpenAiCompatible, "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack words it mentions to the address you enter. Audio never leaves this PC.")]
    [InlineData(CleanupProvider.GitHubCopilot, "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack words it mentions to GitHub. Audio never leaves this PC.")]
    public void Provider_summary_names_the_destination_and_never_audio(CleanupProvider provider, string expected) =>
        Assert.Equal(expected, CleanupDisclosure.SummaryFor(provider));

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("http://localhost:8080/v1", false)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    public void Ollama_and_LM_Studio_on_this_PC_are_summarized_as_staying_on_this_PC(string endpoint, bool onThisPc) =>
        Assert.Equal(
            onThisPc ? CleanupDisclosure.SummaryFor(CleanupProvider.FoundryLocal) : CleanupDisclosure.SummaryFor(CleanupProvider.OpenAiCompatible),
            CleanupDisclosure.SummaryFor(CleanupProvider.OpenAiCompatible, endpoint));

    [Theory]
    [InlineData(CleanupProvider.AzureFoundry, "to your Microsoft Foundry deployment.")]
    [InlineData(CleanupProvider.OpenAiCompatible, "to the AI service you set up.")]
    [InlineData(CleanupProvider.GitHubCopilot, "to GitHub, through your Copilot sign-in.")]
    [InlineData(CleanupProvider.FoundryLocal, "to Foundry Local, which runs on this PC.")]
    public void The_dictionary_suggestion_consent_names_the_recipient_and_the_sample_limit(
        CleanupProvider provider, string destination)
    {
        var text = CleanupDisclosure.SuggestionConsentFor(provider);

        Assert.Contains($"up to {N(AiDictionarySuggester.DefaultMaxSampleChars)} characters", text, StringComparison.Ordinal);
        Assert.Contains("most recent dictations, as they were inserted, " + destination, text, StringComparison.Ordinal);
        Assert.Contains("your dictionary and snippets added", text, StringComparison.Ordinal);
        Assert.Contains("audio are not sent", text, StringComparison.Ordinal);
        Assert.Contains("If where AI cleanup runs changes before the request goes out, nothing is sent.", text, StringComparison.Ordinal);
        Assert.EndsWith("?", CleanupDisclosure.SuggestionConsentTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_disclosure_is_free_of_em_and_en_dashes()
    {
        var texts = new List<string>
        {
            CleanupDisclosure.WhatCleanupSends, CleanupDisclosure.WhatCleanupNeverSends, CleanupDisclosure.SuggestionConsentTitle,
            CleanupDisclosure.WhatTheServiceMayCache, CleanupDisclosure.PromptCachingTitle, CleanupDisclosure.PromptCachingTradeOff,
            CleanupDisclosure.CustomServiceCaching, CleanupDisclosure.CopilotCaching, PromptCachePolicy.Rejected.Display,
        };
        texts.AddRange(Enum.GetValues<CleanupProvider>().Select(CleanupDisclosure.SuggestionConsentFor));
        texts.AddRange(Enum.GetValues<CleanupProvider>().Select(CleanupDisclosure.SummaryFor));

        foreach (var text in texts)
        {
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2013', text);
        }
    }

    [Fact]
    public void The_settings_window_shows_this_wording_instead_of_a_copy_of_its_own()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var code = ReadSettingsWindowCode(root);

        Assert.Contains("{x:Static cleanup:CleanupDisclosure.WhatCleanupSends}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static cleanup:CleanupDisclosure.WhatCleanupNeverSends}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static cleanup:CleanupDisclosure.WhatTheServiceMayCache}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static cleanup:CleanupDisclosure.PromptCachingTradeOff}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static cleanup:CleanupDisclosure.CustomServiceCaching}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static cleanup:CleanupDisclosure.CopilotCaching}", xaml, StringComparison.Ordinal);

        // The switch's title is a literal, so Find a setting's label test can read it, and it is the name every text uses.
        Assert.Contains(
            $"x:Name=\"AiPromptCachingTitle\" Text=\"{CleanupDisclosure.PromptCachingTitle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AI cleanup still receives your vocabulary when this is off.", xaml, StringComparison.Ordinal);
        Assert.Contains("CleanupDisclosure.SuggestionConsentTitle", code, StringComparison.Ordinal);
        Assert.Contains("CleanupDisclosure.SuggestionConsentFor(recipient.Provider)", code, StringComparison.Ordinal);
        Assert.Contains("GlossaryHint.Describe(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_suggestion_consent_is_bound_to_the_recipient_and_the_saved_provider()
    {
        var code = ReadSettingsWindowCode(RepositoryRoot());

        // Asked about the recipient the service serves and the provider actually saved, and the history
        // is sent only to that recipient.
        Assert.Contains("if (_cleanup.Recipient is { } recipient)", code, StringComparison.Ordinal);
        Assert.Contains("AiRequestConsent.IsNeeded(recipient, _savedAiProvider)", code, StringComparison.Ordinal);
        Assert.Contains("_cleanup.CompleteAsync(AiDictionarySuggester.SystemPrompt, sample, recipient)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.AiCleanupProvider != CleanupProvider.FoundryLocal", code, StringComparison.Ordinal);

        // The saved-provider snapshot changes where the document is loaded and right after it is stored,
        // never before: a Save that fails leaves _settings holding the picked provider.
        const string Snapshot = "_savedAiProvider = _settings.AiCleanupProvider;";
        var assignments = Regex.Matches(code, Regex.Escape(Snapshot)).Select(m => m.Index).ToList();
        Assert.Equal(2, assignments.Count);
        var load = code.IndexOf("_settings = settingsRepository.Load();", StringComparison.Ordinal);
        var request = code.IndexOf("private WordPackSaveProtocolRequest BuildWordPackSaveRequest(", StringComparison.Ordinal);
        var store = code.IndexOf("_settingsRepository.SaveBundle(", request, StringComparison.Ordinal);
        var apply = code.IndexOf("useVocabularyReload ? _reloadVocabulary : () => _applySettings(_settings)", request, StringComparison.Ordinal);
        Assert.True(load >= 0 && load < assignments[0] && assignments[0] < store, "The snapshot is not taken where the settings load.");
        Assert.True(store < assignments[1] && store < apply, "The snapshot does not follow the store.");
    }

    [Fact]
    public void Only_the_save_that_stored_the_window_s_document_applies_it()
    {
        var root = RepositoryRoot();
        var code = ReadSettingsWindowCode(root);

        // One call puts settings into effect from the window, the successful Save's: after the store, before the
        // handlers of a Save that failed. A failed Save leaves every edit in _settings, a picked provider among them,
        // so any other caller would apply what nothing stored.
        var call = Assert.Single(Regex.Matches(code, @"_applySettings\s*(\(|\?\.|\.Invoke\b)"));
        Assert.StartsWith("_applySettings(_settings)", code[call.Index..], StringComparison.Ordinal);
        var save = code.IndexOf("private async Task<bool> TrySaveAsync()", StringComparison.Ordinal);
        var request = code.IndexOf("private WordPackSaveProtocolRequest BuildWordPackSaveRequest(", StringComparison.Ordinal);
        var store = code.IndexOf("_settingsRepository.SaveBundle(", request, StringComparison.Ordinal);
        var protocolCall = code.IndexOf("_wordPackSaveProtocol.SaveAsync(", save, StringComparison.Ordinal);
        Assert.True(
            save >= 0 && protocolCall > save && store < call.Index,
            "The window applies its own document outside the successful Save.");

        // Nor is the delegate handed on another way: besides its field, its assignment and that call, it only goes to
        // StoredSettingsReapply, which applies the settings as stored. Both calls keep the answer of the vocabulary
        // generation they ask for, which the window awaits before it says the change is in effect.
        var uses = code.Split('\n').Select(line => line.Trim()).Where(line => Regex.IsMatch(line, @"\b_applySettings\b")).ToList();
        Assert.All(uses, line => Assert.True(
            line is "private readonly Func<AppSettings, Task<Scribe.Core.Vocabulary.VocabularyRefresh>> _applySettings;" or "_applySettings = applySettings;" ||
            line is "useVocabularyReload ? _reloadVocabulary : () => _applySettings(_settings)," ||
            line.StartsWith("var reapplied = StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, ", StringComparison.Ordinal),
            $"The window uses _applySettings in a way this test does not know: {line}"));

        // The Usage page's Add applies the stored settings, and the shell reloads only the vocabulary when there are none.
        var add = code.IndexOf("private async void UsageNovelTermAddButton_Click(", StringComparison.Ordinal);
        var next = code.IndexOf("private void RefreshUsageInsightAvailability()", add, StringComparison.Ordinal);
        Assert.True(add >= 0 && next > add, "The Usage page's Add handler was not found.");
        Assert.Contains(
            "StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary);",
            code[add..next],
            StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "App.xaml.cs"));
        Assert.Contains("() => _controller!.ReloadVocabulary(),", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_privacy_policy_states_what_cleanup_sends_with_the_limits_the_code_enforces()
    {
        var policy = Flatten(File.ReadAllText(Path.Combine(RepositoryRoot(), "PRIVACY.md")));

        Assert.Contains("every cleanup request sends that provider", policy, StringComparison.Ordinal);
        Assert.Contains("the word packs you let AI cleanup use", policy, StringComparison.Ordinal);
        Assert.Contains("that the dictation appears to mention", policy, StringComparison.Ordinal);
        Assert.Contains("including words it heard slightly differently, so an entry the dictation does not mention is not sent with it", policy, StringComparison.Ordinal);
        Assert.Contains("Versions before 0.5.2 sent every entry with every request, whether or not the dictation mentioned it.", policy, StringComparison.Ordinal);
        Assert.Contains("This does not depend on whether \"Apply your dictionary and snippets\" is turned on", policy, StringComparison.Ordinal);
        Assert.Contains(
            $"up to {N(CleanupPrompt.MaxGlossaryTermsCloud)} terms and {N(CleanupPrompt.MaxGlossaryChars)} characters " +
            $"({N(CleanupPrompt.MaxGlossaryTermsLocal)} terms when AI cleanup uses the short instructions)",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            $"each spoken form put on one line and shortened to {N(CleanupPrompt.MaxGlossaryTermChars)} characters",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            $"An entry whose written form spans more than one line or runs past {N(CleanupPrompt.MaxGlossaryTermChars)} " +
            "characters, such as a signature or an address, is not vocabulary: the dictionary still applies it on this " +
            "PC, but it is not sent.",
            policy, StringComparison.Ordinal);
        Assert.Contains("with none of your vocabulary", policy, StringComparison.Ordinal);
        Assert.Contains("AI cleanup never sends audio, your snippet templates", policy, StringComparison.Ordinal);
        Assert.Contains(
            $"up to {N(AiDictionarySuggester.DefaultMaxSampleChars)} characters of your most recent dictations",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            "if where AI cleanup runs changes before the request goes out, nothing is sent", policy, StringComparison.Ordinal);
        Assert.Contains(
            $"spans more than one line or is longer than {N(CleanupPrompt.MaxGlossaryTermChars)} characters",
            policy, StringComparison.Ordinal);
        Assert.Contains("the database overwrites the deleted content with zeros", policy, StringComparison.Ordinal);
        Assert.Contains(
            "until Scribe copies that log into `scribe.db` and empties it, either file can still hold an earlier copy",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            "Storage maintenance does both at the end of its next pass: normally within a minute of your deleting " +
            "history or clearing the cleanup failure samples, and at the end of the pass that removes something " +
            "because its retention period ended.",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            "it does not empty the log until it tries again: two minutes later at first, twice as long after each " +
            "further interruption, and never more than an hour later.",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            "maintenance tries again shortly after, up to three times, and then hourly.", policy, StringComparison.Ordinal);

        // A normal close only tries: SQLite reports a TRUNCATE checkpoint busy, and leaves the log, while another
        // connection still uses it, and the close skips the checkpoint when it cannot have the write gate in time.
        Assert.Contains(
            "Scribe also tries to empty the log when it closes normally, but if the database is still in use then, or " +
            "the attempt does not succeed, an earlier copy can stay in the log until the log is next emptied.",
            policy, StringComparison.Ordinal);
        Assert.DoesNotContain("empties the log when it closes", policy, StringComparison.Ordinal);
        Assert.Contains(
            "Secure delete applies to everything Scribe deletes from its database, dictionary entries, snippets and " +
            "profiles included, but Scribe does not empty the log specially after those deletions",
            policy, StringComparison.Ordinal);

        // The numbers those sentences quote are the ones maintenance runs with. "Within a minute": a deletion
        // brings the pass forward by TriggerDelay, and it waits at most MinimumSpacing after the last pass.
        var maintenance = StorageMaintenanceOptions.Default;
        Assert.True(maintenance.TriggerDelay + maintenance.MinimumSpacing < TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(2), maintenance.ConversionQuietPeriod);
        Assert.Equal(TimeSpan.FromHours(1), maintenance.Interval);
        Assert.Equal(3, StorageMaintenance.MaxQuickReclaimRetries);

        Assert.Contains("Scribe versions up to 0.4.3 did not overwrite deleted content", policy, StringComparison.Ordinal);
        Assert.Contains("does not reach copies made elsewhere", policy, StringComparison.Ordinal);
        Assert.Contains(
            "For Microsoft Foundry, Scribe asks the service not to store its responses, but Microsoft's abuse " +
            "monitoring can still keep a sample of prompts and responses it flags for review",
            policy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_cache_is_disclosed_with_what_microsoft_documents_and_what_the_setting_asks_for()
    {
        // The facts, from https://learn.microsoft.com/azure/foundry/openai/how-to/prompt-caching (updated 2026-08-12): a
        // cached prefix "remains eligible for reuse for at least 30 minutes" on GPT-5.6 and later; extended retention keeps
        // prefixes "up to a maximum of 24 hours"; the FAQ "Can I disable prompt caching?": "On Standard pay-as-you-go
        // deployments with GPT-5.6 models and later model families, set prompt_cache_options.mode to explicit and don't add
        // any explicit breakpoints. The request doesn't use prompt caching or incur cache-write charges. Earlier models and
        // PTU-M deployments don't support this option; prompt caching remains enabled by default."; models before GPT-5.6
        // "return a 400 error"; caches are not shared "between Azure subscriptions". What Scribe does is ask, for new
        // requests: it clears nothing, and enforces nothing on the service's side (PLAT-R-04).
        Assert.Equal(
            "On: Microsoft Foundry may reuse parts of recent requests to respond faster, and may keep temporary data derived " +
            "from them, including your dictation, the instructions and your vocabulary, for at least 30 minutes (up to 24 " +
            "hours on some models). Off: Scribe asks Microsoft Foundry not to use its prompt cache for new cleanup requests. " +
            "AI cleanup can be slower, and Scribe can't clear what the cache already holds. Off works on GPT-5.6 and later " +
            "models on Standard deployments; earlier models and provisioned deployments can't turn caching off, so AI " +
            "cleanup stops and Scribe types what it hears until you turn this back on.",
            CleanupDisclosure.PromptCachingTradeOff);

        var card = CleanupDisclosure.WhatTheServiceMayCache;
        Assert.Contains("does not turn off its separate prompt cache", card, StringComparison.Ordinal);
        Assert.Contains("the dictation, the instructions and your vocabulary, for at least 30 minutes (up to 24 hours on some models)", card, StringComparison.Ordinal);
        Assert.Contains(
            $"Turning off \"{CleanupDisclosure.PromptCachingTitle}\" asks Microsoft Foundry not to use its prompt cache for new cleanup requests.",
            card,
            StringComparison.Ordinal);
        Assert.Contains("GPT-5.6 and later models on Standard deployments", card, StringComparison.Ordinal);
        Assert.Contains("earlier models and provisioned deployments can't turn caching off", card, StringComparison.Ordinal);
        Assert.Contains("Scribe can't clear what the cache already holds", card, StringComparison.Ordinal);
        Assert.Contains("Another AI service and GitHub Copilot follow their own caching policy", card, StringComparison.Ordinal);

        // The status a refusal shows says this deployment can't, and names the setting to turn back on and what can. The
        // consequence (Scribe types what it hears) is each surface's to add, as for every cleanup reason.
        var rejected = PromptCachePolicy.Rejected.Display;
        Assert.StartsWith("This deployment can't turn caching off.", rejected, StringComparison.Ordinal);
        Assert.Contains($"\"{CleanupDisclosure.PromptCachingTitle}\" back on", rejected, StringComparison.Ordinal);
        Assert.Contains("a GPT-5.6 or later model on a Standard deployment", rejected, StringComparison.Ordinal);

        var policy = Flatten(File.ReadAllText(Path.Combine(RepositoryRoot(), "PRIVACY.md")));
        foreach (var fact in new[]
                 {
                     "Asking Microsoft Foundry not to store responses does not turn off its separate prompt cache.",
                     $"With \"{CleanupDisclosure.PromptCachingTitle}\" on, which is the default,",
                     "including the dictation, the cleanup instructions and your vocabulary",
                     "newer models keep a cached prefix for at least 30 minutes and possibly longer",
                     "some models keep cached data for up to 24 hours",
                     "prompt caches are not shared between Azure subscriptions",
                     "Scribe cannot clear what the cache already holds.",
                     "When you turn the setting off, Scribe asks Microsoft Foundry not to use its prompt cache for new cleanup requests",
                     "asks for the documented mode that does not use prompt caching",
                     "GPT-5.6 and later models on Standard deployments",
                     "earlier models and provisioned (PTU-M) deployments don't support it, so they can't turn caching off",
                     "(https://learn.microsoft.com/azure/foundry/openai/how-to/prompt-caching)",
                     "When a deployment refuses the option, AI cleanup does not run and Scribe types what it heard",
                     "Scribe does not send the request again without the option",
                     "Another AI service and GitHub Copilot follow their own caching policy",
                     $"Turn off \"{CleanupDisclosure.PromptCachingTitle}\" so that Scribe asks Microsoft Foundry not to use its prompt cache for new cleanup requests",
                     "earlier models and provisioned deployments can't turn caching off",
                 })
        {
            Assert.Contains(fact, policy, StringComparison.Ordinal);
        }

        var readme = Flatten(File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")));
        Assert.Contains(
            $"Turning off {CleanupDisclosure.PromptCachingTitle} asks Microsoft Foundry not to use its prompt cache for new cleanup requests",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"Turning off {CleanupDisclosure.PromptCachingTitle} asks it not to use that cache for new cleanup requests", readme, StringComparison.Ordinal);
        Assert.Contains("earlier models and provisioned deployments can't turn caching off", readme, StringComparison.Ordinal);
        Assert.Contains("Scribe can't clear what the cache already holds", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void No_text_claims_turning_caching_off_keeps_nothing_or_that_unsupported_deployments_always_cache()
    {
        // The option asks, for new requests, where it is supported; it clears nothing and the service decides what an
        // unsupported deployment does ("prompt caching remains enabled by default" is not "every request is cached").
        var texts = new List<string>
        {
            CleanupDisclosure.WhatTheServiceMayCache,
            CleanupDisclosure.PromptCachingTradeOff,
            PromptCachePolicy.Rejected.Display,
            File.ReadAllText(Path.Combine(RepositoryRoot(), "PRIVACY.md")),
            File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")),
            File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml")),
        };

        foreach (var text in texts.Select(Flatten))
        {
            foreach (var claim in new[] { "always cache", "none of that is kept", "stops that on", "neither reads nor writes" })
            {
                Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_readme_says_what_store_false_does_and_does_not_do()
    {
        var readme = Flatten(File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")));

        Assert.Contains("asks Microsoft Foundry not to store the response", readme, StringComparison.Ordinal);
        Assert.Contains("abuse monitoring can still keep a sample of flagged prompts and responses for review", readme, StringComparison.Ordinal);
    }

    public static TheoryData<string> UserFacingDocuments => new()
    {
        "PRIVACY.md",
        "README.md",
        "AGENTS.md",
        ".claude/skills/scribe-code-review/agents/privacy-egress.md",
        "docs/foundry-setup.md",
        "docs/service-principal-setup.md",
        "docs/microsoft-store-submission.md",
        "src/Scribe.App/Settings/SettingsWindow.xaml",
    };

    [Theory]
    [MemberData(nameof(UserFacingDocuments))]
    public void No_user_facing_text_claims_cleanup_sends_only_the_transcript_or_only_relevant_terms(string relativePath)
    {
        var text = Flatten(File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath)));

        foreach (var claim in new[]
                 {
                     "relevant dictionary",
                     "relevant glossary",
                     "transcribed text only",
                     "only the transcribed",
                     "transcript text only",
                     "only transcript text",
                     "turns request storage off",
                 })
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Markdown wraps lines and emphasizes words, so a phrase is looked for in the text as it reads.
    private static string Flatten(string markdown) =>
        Regex.Replace(markdown.Replace("*", string.Empty, StringComparison.Ordinal), @"\s+", " ");

    private static string ReadSettingsWindowCode(string root) =>
        string.Join(
            '\n',
            Directory.GetFiles(Path.Combine(root, "src", "Scribe.App", "Settings"), "SettingsWindow*.cs")
                .OrderBy(path => Path.GetFileName(path).Equals("SettingsWindow.xaml.cs", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
