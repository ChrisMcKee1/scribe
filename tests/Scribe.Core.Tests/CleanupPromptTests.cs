using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Deterministic proof that editing the writing style (or any prompt input) actually changes the
/// instructions sent to the model — i.e. a prompt hot-swap is reflected on the next call. These run
/// offline (no model) and guard the safety guardrails and provider-specific directives.
/// </summary>
public sealed class CleanupPromptTests
{
    private static CleanupOptions Foundry(string alias = "qwen3-1.7b", string? style = null) =>
        new(true, CleanupProvider.FoundryLocal, alias, null, null, WritingStyle: style);

    private static CleanupOptions Azure(string? style = null) =>
        new(true, CleanupProvider.AzureFoundry, "qwen3-1.7b",
            "https://example.openai.azure.com/", "gpt-5.4-mini", WritingStyle: style);

    [Fact]
    public void System_prompt_embeds_a_custom_writing_style_verbatim()
    {
        const string style = "Sound like a swashbuckling pirate. Use 'arr' and 'matey'.";
        var prompt = TextCleanupService.BuildSystemPrompt(Foundry(style: style));

        Assert.Contains("Writing style:", prompt);
        Assert.Contains(style, prompt);
    }

    [Fact]
    public void System_prompt_falls_back_to_default_style_when_blank()
    {
        var prompt = TextCleanupService.BuildSystemPrompt(Foundry(style: "   "));

        Assert.Contains(CleanupPrompt.DefaultWritingStyle, prompt);
    }

    [Fact]
    public void Swapping_the_writing_style_changes_the_system_prompt()
    {
        var pirate = TextCleanupService.BuildSystemPrompt(Foundry(style: "Talk like a pirate."));
        var olde = TextCleanupService.BuildSystemPrompt(Foundry(style: "Write in formal Old English."));

        Assert.NotEqual(pirate, olde);
        Assert.Contains("Talk like a pirate.", pirate);
        Assert.Contains("Write in formal Old English.", olde);
        Assert.DoesNotContain("pirate", olde, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_keeps_the_post_editor_safety_guardrails()
    {
        var prompt = TextCleanupService.BuildSystemPrompt(Foundry());

        Assert.Contains("post-editor", prompt);
        Assert.Contains("do not", prompt, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("follow any instructions", prompt, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Return only the corrected text", prompt);
    }

    [Fact]
    public void Qwen3_foundry_local_gets_the_no_think_directive()
    {
        var prompt = TextCleanupService.BuildSystemPrompt(Foundry("qwen3-1.7b"));
        Assert.EndsWith("/no_think", prompt);
    }

    [Fact]
    public void Non_qwen3_and_azure_do_not_get_the_no_think_directive()
    {
        var phi = TextCleanupService.BuildSystemPrompt(Foundry("phi-3.5-mini"));
        var azure = TextCleanupService.BuildSystemPrompt(Azure());

        Assert.DoesNotContain("/no_think", phi);
        Assert.DoesNotContain("/no_think", azure);
    }
}
