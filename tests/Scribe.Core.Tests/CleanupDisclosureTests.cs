using System.Globalization;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
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
        foreach (var provider in new[] { "Microsoft Foundry", "OpenAI-compatible endpoint", "GitHub Copilot" })
        {
            Assert.Contains(provider, text, StringComparison.Ordinal);
        }

        Assert.Contains("with every cleanup request", text, StringComparison.Ordinal);
        Assert.Contains("the text Scribe recognized for that dictation", text, StringComparison.Ordinal);
        Assert.Contains("writing style", text, StringComparison.Ordinal);
        Assert.Contains("enabled dictionary and library terms", text, StringComparison.Ordinal);
        Assert.Contains(
            $"up to {N(CleanupPrompt.MaxGlossaryTermsCloud)} terms and {N(CleanupPrompt.MaxGlossaryChars)} characters",
            text, StringComparison.Ordinal);
        Assert.Contains($"{N(CleanupPrompt.MaxGlossaryTermsLocal)} terms with the Local prompt style", text, StringComparison.Ordinal);
        Assert.Contains("whether or not the dictation mentions them", text, StringComparison.Ordinal);
        Assert.DoesNotContain("relevant", text, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void The_dictionary_suggestion_consent_quotes_the_sample_limit_the_code_enforces()
    {
        var text = CleanupDisclosure.SuggestionConsent;

        Assert.Contains($"up to {N(AiDictionarySuggester.DefaultMaxSampleChars)} characters", text, StringComparison.Ordinal);
        Assert.Contains("most recent dictations, as they were inserted", text, StringComparison.Ordinal);
        Assert.Contains("your dictionary and snippets added", text, StringComparison.Ordinal);
        Assert.Contains("audio are not sent", text, StringComparison.Ordinal);
        Assert.EndsWith("?", CleanupDisclosure.SuggestionConsentTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_disclosure_is_free_of_em_and_en_dashes()
    {
        foreach (var text in new[]
                 {
                     CleanupDisclosure.WhatCleanupSends, CleanupDisclosure.WhatCleanupNeverSends,
                     CleanupDisclosure.SuggestionConsentTitle, CleanupDisclosure.SuggestionConsent,
                 })
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
        var code = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains("{x:Static cleanup:CleanupDisclosure.WhatCleanupSends}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static cleanup:CleanupDisclosure.WhatCleanupNeverSends}", xaml, StringComparison.Ordinal);
        Assert.Contains("AI cleanup still receives your vocabulary when this is off.", xaml, StringComparison.Ordinal);
        Assert.Contains("CleanupDisclosure.SuggestionConsentTitle", code, StringComparison.Ordinal);
        Assert.Contains("CleanupDisclosure.SuggestionConsent,", code, StringComparison.Ordinal);
        Assert.Contains("GlossaryHint.Describe(", code, StringComparison.Ordinal);

        // The consent is asked by the provider the request will reach, which is the saved one.
        Assert.Contains("_settings.AiCleanupProvider != CleanupProvider.FoundryLocal &&", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_privacy_policy_states_what_cleanup_sends_with_the_limits_the_code_enforces()
    {
        var policy = Flatten(File.ReadAllText(Path.Combine(RepositoryRoot(), "PRIVACY.md")));

        Assert.Contains("every cleanup request sends that provider", policy, StringComparison.Ordinal);
        Assert.Contains("whether or not the dictation mentions any of it", policy, StringComparison.Ordinal);
        Assert.Contains("whether or not post-processing is switched on", policy, StringComparison.Ordinal);
        Assert.Contains(
            $"up to {N(CleanupPrompt.MaxGlossaryTermsCloud)} terms and {N(CleanupPrompt.MaxGlossaryChars)} characters " +
            $"({N(CleanupPrompt.MaxGlossaryTermsLocal)} terms when the Local prompt style is in use)",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            $"each form put on one line and shortened to {N(CleanupPrompt.MaxGlossaryTermChars)} characters",
            policy, StringComparison.Ordinal);
        Assert.Contains("with none of your vocabulary", policy, StringComparison.Ordinal);
        Assert.Contains("AI cleanup never sends audio, your snippet templates", policy, StringComparison.Ordinal);
        Assert.Contains(
            $"up to {N(AiDictionarySuggester.DefaultMaxSampleChars)} characters of your most recent dictations",
            policy, StringComparison.Ordinal);
        Assert.Contains(
            $"spans more than one line or is longer than {N(CleanupPrompt.MaxGlossaryTermChars)} characters",
            policy, StringComparison.Ordinal);
        Assert.Contains("overwrites the deleted content inside its file with zeros", policy, StringComparison.Ordinal);
    }

    public static TheoryData<string> UserFacingDocuments => new()
    {
        "PRIVACY.md",
        "README.md",
        "AGENTS.md",
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
                 })
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Markdown wraps lines and emphasizes words, so a phrase is looked for in the text as it reads.
    private static string Flatten(string markdown) =>
        Regex.Replace(markdown.Replace("*", string.Empty, StringComparison.Ordinal), @"\s+", " ");

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
