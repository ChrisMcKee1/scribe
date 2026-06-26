using System.Collections.Generic;
using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Proves the Azure deployment filter only surfaces models that can actually run the Responses-API
/// text-cleanup path. The decisive signal is the per-deployment capability map Azure returns:
/// <c>realtime=true</c> is excluded outright (gpt-realtime/-1.5 400 on the Responses text call),
/// <c>responses=true</c> is the authoritative include (newer reasoning/agent models such as
/// <c>gpt-5.4-pro</c> and <c>gpt-5.3-codex</c> report <c>chatCompletion=false</c> but
/// <c>responses=true</c> and DO work), and otherwise <c>chatCompletion</c> gates older chat models.
/// When the map is absent we fall back to the older model-name heuristic.
/// </summary>
public sealed class AzureDeploymentFilterTests
{
    private static Dictionary<string, string> Caps(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            map[key] = value;
        }

        return map;
    }

    [Fact]
    public void Realtime_model_is_excluded_by_capabilities()
    {
        // The user's real deployment: realtime=true, chatCompletion=false.
        var caps = Caps(("area", "US"), ("assistants", "false"), ("chatCompletion", "false"),
            ("completion", "false"), ("realtime", "true"));

        Assert.False(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-realtime-1.5", "gpt-realtime-1.5"));
    }

    [Fact]
    public void Chat_model_is_included_by_capabilities()
    {
        var caps = Caps(("chatCompletion", "true"), ("completion", "false"));

        Assert.True(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-4.1-mini", "gpt-4.1-mini"));
    }

    [Fact]
    public void Reasoning_model_is_included_by_responses_capability()
    {
        // Live ground truth: gpt-5.4-pro reports chatCompletion=false but responses=true and works.
        var caps = Caps(("agentsV2", "true"), ("assistants", "true"), ("chatCompletion", "false"),
            ("responses", "true"));

        Assert.True(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-5.4-pro", "my-reasoner"));
    }

    [Fact]
    public void Codex_model_with_responses_only_is_included()
    {
        // gpt-5.3-codex: chatCompletion=false, responses=true (no chat flag) -> include via responses.
        var caps = Caps(("agentsV2", "true"), ("area", "US"), ("chatCompletion", "false"),
            ("responses", "true"));

        Assert.True(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-5.3-codex", "codex"));
    }

    [Fact]
    public void Realtime_is_excluded_even_when_responses_flag_present()
    {
        // realtime is checked first, so a realtime model is hidden regardless of other flags.
        var caps = Caps(("realtime", "true"), ("responses", "true"), ("chatCompletion", "true"));

        Assert.False(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-realtime", "realtime"));
    }

    [Fact]
    public void Capability_map_excludes_when_chat_false_and_no_responses()
    {
        // e.g. mistral-document-ai: capabilities present, chatCompletion=false, no responses key.
        var caps = Caps(("chatCompletion", "false"));

        Assert.False(AzureFoundryDiscovery.SupportsTextCleanup(caps, "mistral-document-ai", "doc-ai"));
    }

    [Fact]
    public void Capability_flag_wins_over_chat_sounding_name()
    {
        // Even a "gpt"-named deployment is hidden when the map says it can't chat.
        var caps = Caps(("chatCompletion", "false"), ("realtime", "true"));

        Assert.False(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-realtime", "gpt-deployment"));
    }

    [Fact]
    public void Capability_flag_is_case_insensitive()
    {
        var caps = Caps(("ChatCompletion", "True"));

        Assert.True(AzureFoundryDiscovery.SupportsTextCleanup(caps, "gpt-4o", "gpt-4o"));
    }

    [Theory]
    [InlineData("text-embedding-3-large")]
    [InlineData("whisper")]
    [InlineData("dall-e-3")]
    public void Name_heuristic_excludes_non_chat_models_when_capabilities_absent(string modelName)
    {
        Assert.False(AzureFoundryDiscovery.SupportsTextCleanup(null, modelName, modelName));
    }

    [Fact]
    public void Name_heuristic_includes_chat_models_when_capabilities_absent()
    {
        Assert.True(AzureFoundryDiscovery.SupportsTextCleanup(null, "gpt-4o-mini", "gpt-4o-mini"));
    }

    [Fact]
    public void Empty_capability_map_falls_back_to_name_heuristic()
    {
        var empty = new Dictionary<string, string>();

        Assert.False(AzureFoundryDiscovery.SupportsTextCleanup(empty, "text-embedding-ada-002", "embeddings"));
        Assert.True(AzureFoundryDiscovery.SupportsTextCleanup(empty, "gpt-4.1", "chat"));
    }
}
