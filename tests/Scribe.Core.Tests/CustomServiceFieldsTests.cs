using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The OpenAI-compatible service's fields under "On this PC": Ollama and LM Studio stored at their own address, another AI
/// service remembered beside them, and a saved key never lost (code review of the local model memory changes, issue 1).
/// </summary>
public sealed class CustomServiceFieldsTests
{
    private static AppSettings Saved(string? endpoint, string? model, string? key, CleanupProvider provider = CleanupProvider.OpenAiCompatible)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = provider;
        settings.AiCleanupCustomEndpoint = endpoint;
        settings.AiCleanupCustomModel = model;
        settings.AiCleanupCustomApiKey = key;
        return settings;
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", null, LocalServerApp.Ollama)]
    [InlineData("http://127.0.0.1:11434/v1/", null, LocalServerApp.Ollama)]
    [InlineData("http://localhost:1234/v1", "  ", LocalServerApp.LmStudio)]
    [InlineData("http://localhost:1234/v1", "lm-studio-token", LocalServerApp.None)]
    [InlineData("http://localhost:11434/v1", "ollama", LocalServerApp.None)]
    [InlineData("https://openrouter.ai/api/v1", null, LocalServerApp.None)]
    public void An_app_s_own_address_saved_without_a_key_is_that_app(string endpoint, string? key, LocalServerApp expected) =>
        Assert.Equal(expected, CustomServiceFields.SavedApp(Saved(endpoint, "model", key)));

    [Fact]
    public void Only_the_OpenAI_compatible_service_is_ever_an_app() =>
        Assert.Equal(
            LocalServerApp.None,
            CustomServiceFields.SavedApp(Saved("http://localhost:11434/v1", "gemma4:e4b", null, CleanupProvider.FoundryLocal)));

    [Fact]
    public void A_service_that_needs_a_key_stays_another_AI_service_and_saving_keeps_its_key()
    {
        // 0.5.1 saved LM Studio with "Require Authentication" on as another AI service, token and all.
        var saved = Saved("http://localhost:1234/v1", "qwen/qwen3-4b-2507", "lm-studio-token");
        var boxes = CustomServiceFields.OtherService(saved);
        Assert.Equal(new CustomServiceFields.Fields("http://localhost:1234/v1", "qwen/qwen3-4b-2507", "lm-studio-token"), boxes);

        var (stored, remembered) = CustomServiceFields.ForSave(LocalServerApp.None, null, boxes, saved);

        Assert.Equal(boxes, stored);
        Assert.Equal(CustomServiceFields.Fields.None, remembered);
    }

    [Fact]
    public void Choosing_Ollama_remembers_another_AI_service_and_choosing_it_again_brings_it_back()
    {
        var saved = Saved("https://openrouter.ai/api/v1", "openai/gpt-5-mini", "sk-or-key");
        var boxes = CustomServiceFields.OtherService(saved);

        var (stored, remembered) = CustomServiceFields.ForSave(LocalServerApp.Ollama, "gemma4:e4b", boxes, saved);

        Assert.Equal(new CustomServiceFields.Fields(LocalAiServer.OllamaAddress, "gemma4:e4b", null), stored);
        Assert.Equal(boxes, remembered);

        // Saved that way, Settings opens on Ollama, and the Another AI service boxes show OpenRouter again.
        var next = Saved(stored.Endpoint, stored.Model, stored.ApiKey);
        next.AiCleanupOtherServiceEndpoint = remembered.Endpoint;
        next.AiCleanupOtherServiceModel = remembered.Model;
        next.AiCleanupOtherServiceApiKey = remembered.ApiKey;
        Assert.Equal(LocalServerApp.Ollama, CustomServiceFields.SavedApp(next));
        Assert.Equal("gemma4:e4b", CustomServiceFields.SavedAppModel(next));
        Assert.Equal(boxes, CustomServiceFields.OtherService(next));

        // Choosing it again stores it to run cleanup, and nothing is remembered.
        var (back, none) = CustomServiceFields.ForSave(LocalServerApp.None, "gemma4:e4b", CustomServiceFields.OtherService(next), next);
        Assert.Equal(boxes, back);
        Assert.Equal(CustomServiceFields.Fields.None, none);
    }

    [Fact]
    public void An_app_keeps_the_address_it_was_saved_at_so_opening_and_saving_changes_nothing()
    {
        var saved = Saved("http://127.0.0.1:11434/v1/", "llama3.2", null);

        var (stored, remembered) = CustomServiceFields.ForSave(LocalServerApp.Ollama, "llama3.2", CustomServiceFields.OtherService(saved), saved);

        Assert.Equal(new CustomServiceFields.Fields("http://127.0.0.1:11434/v1/", "llama3.2", null), stored);
        Assert.Equal(CustomServiceFields.Fields.None, remembered);

        // Another app takes its own address.
        Assert.Equal(
            LocalAiServer.LmStudioAddress,
            CustomServiceFields.ForSave(LocalServerApp.LmStudio, "google/gemma-4-e2b", CustomServiceFields.Fields.None, saved).Stored.Endpoint);
    }

    [Fact]
    public void Save_trims_what_it_stores_and_stores_blank_as_nothing()
    {
        var saved = Saved(null, null, null, CleanupProvider.FoundryLocal);

        var (stored, remembered) = CustomServiceFields.ForSave(
            LocalServerApp.LmStudio, "  ", new(" https://api.example.com/v1 ", " my-model ", " key "), saved);

        Assert.Equal(new CustomServiceFields.Fields(LocalAiServer.LmStudioAddress, null, null), stored);
        Assert.Equal(new CustomServiceFields.Fields("https://api.example.com/v1", "my-model", "key"), remembered);
        Assert.Equal(
            CustomServiceFields.Fields.None,
            CustomServiceFields.ForSave(LocalServerApp.None, null, new("  ", null, ""), saved).Stored);
    }

    [Fact]
    public void Nothing_is_remembered_for_a_service_that_runs_cleanup_itself()
    {
        var saved = Saved("https://openrouter.ai/api/v1", "openai/gpt-5-mini", "sk-or-key");
        saved.AiCleanupOtherServiceEndpoint = "https://stale.example.com/v1";

        // The boxes show the service saved to run cleanup, never a stale remembered one.
        Assert.Equal("https://openrouter.ai/api/v1", CustomServiceFields.OtherService(saved).Endpoint);
        Assert.Null(CustomServiceFields.SavedAppModel(saved));
    }
}
