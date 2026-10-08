using System.Net;
using OllamaSharp.Models.Exceptions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class OllamaModelDownloadTests
{
    [Fact]
    public void The_catalog_resource_is_a_Uri_for_the_compiled_WPF_hyperlink() =>
        Assert.Equal(new Uri("https://ollama.com/search"), OllamaModelDownload.CatalogUri);

    [Theory]
    [InlineData(" gemma4:12b ", "gemma4:12b")]
    [InlineData("gemma4", "gemma4")]
    [InlineData("namespace/model-name:Q4_K_M", "namespace/model-name:Q4_K_M")]
    [InlineData("deepseek-r1", "deepseek-r1")]
    [InlineData("VicRodger27/Writex", "VicRodger27/Writex")]
    [InlineData("VicRodger27/Writex:4b", "VicRodger27/Writex:4b")]
    [InlineData(" ollama run VicRodger27/Writex:4b ", "VicRodger27/Writex:4b")]
    [InlineData("ollama pull deepseek-r1", "deepseek-r1")]
    [InlineData("OLLAMA RUN deepseek-r1:8b", "deepseek-r1:8b")]
    public void Valid_model_names_are_trimmed_not_rewritten(string input, string expected)
    {
        var request = OllamaModelDownload.Validate(input);
        Assert.Equal(expected, request.Model);
        Assert.Null(request.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("gemma4 12b")]
    [InlineData("https://ollama.com/library/gemma4")]
    [InlineData("../gemma4")]
    [InlineData("host:11434/model")]
    [InlineData("gemma4:12b\nanother")]
    [InlineData("gemma4:cloud")]
    [InlineData("gemma4:31b-CLOUD")]
    [InlineData("ollama run gemma4:cloud")]
    [InlineData("ollama pull gemma4:31b-cloud")]
    [InlineData("ollama run")]
    [InlineData("ollama run VicRodger27/Writex:4b --verbose")]
    [InlineData("ollama run VicRodger27/Writex:4b; shutdown")]
    [InlineData("ollama run deepseek-r1\nollama run other-model")]
    public void Invalid_names_have_an_explicit_reason(string? input)
    {
        var request = OllamaModelDownload.Validate(input);
        Assert.Null(request.Model);
        Assert.False(string.IsNullOrWhiteSpace(request.Error));
    }

    [Fact]
    public void A_model_name_is_bounded()
    {
        Assert.Null(OllamaModelDownload.Validate(new string('a', 201)).Model);
        Assert.NotNull(OllamaModelDownload.Validate(new string('a', 200)).Model);
        Assert.Null(OllamaModelDownload.Validate("ollama run " + new string('a', 201)).Model);
        Assert.NotNull(OllamaModelDownload.Validate("ollama run " + new string('a', 200)).Model);
    }

    [Fact]
    public void Progress_does_not_call_a_file_with_no_bytes_downloaded_one_megabyte()
    {
        var message = OllamaModelDownload.Describe(
            new(OllamaDownloadStage.Downloading, 0, 1024 * 1024 * 1024, 0));
        Assert.Contains("0 MB", message, StringComparison.Ordinal);
        Assert.Contains("1.0 GB", message, StringComparison.Ordinal);
        Assert.Contains("0%", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_notices_never_quote_the_server_or_exception_text()
    {
        foreach (var failure in new Exception[]
        {
            new HttpRequestException("private host or response", null, HttpStatusCode.NotFound),
            new HttpRequestException("private credentials", null, HttpStatusCode.Unauthorized),
            new HttpRequestException(HttpRequestError.ConnectionError, "private host"),
            new InvalidDataException("private downloaded data"),
            new OperationCanceledException("private cancellation"),
            new ResponseError("private server text: pull model manifest: file does not exist"),
            new ResponseError("pull model manifest: file does not exist, private server text"),
        })
        {
            Assert.DoesNotContain("private", OllamaModelDownload.Failure(failure), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("private server text: pull model manifest: file does not exist")]
    [InlineData("pull model manifest: file does not exist, private server text")]
    [InlineData("unknown model or private access")]
    public void Arbitrary_stream_error_text_is_never_used_to_guess_a_missing_tag(string message) =>
        Assert.Equal(
            "Ollama couldn't finish the download. Check that it's open and you have enough disk space, then try again.",
            OllamaModelDownload.Failure(new ResponseError(message)));

    [Theory]
    [InlineData("https://example.invalid/v1", CustomApiStyle.Responses)]
    [InlineData("http://127.0.0.1:11434/v1/", CustomApiStyle.ChatCompletions)]
    public void Switching_saves_only_the_Ollama_choice_and_keeps_another_service_including_its_key(
        string endpoint, CustomApiStyle rememberedApi)
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(database);
        var original = AppSettings.CreateDefault();
        original.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        original.AiCleanupCustomEndpoint = endpoint;
        original.AiCleanupCustomModel = "saved-service-model";
        original.AiCleanupCustomApiKey = "synthetic-current-service-key";
        original.AiCleanupOtherServiceApiKey = "synthetic-obsolete-service-key";
        original.AiCleanupCustomApiStyle = CustomApiStyle.Responses;
        original.AiCleanupWritingStyle = "saved writing style";
        original.AiCleanupOllamaContextTokens = 32768;
        original.EnableAiCleanup = false;
        original.HistoryRetentionDays = 90;
        repository.Save(original);
        var draft = original.Clone();
        draft.AiCleanupWritingStyle = "unsaved writing style";
        draft.HistoryRetentionDays = 7;
        draft.EnableAiCleanup = true;
        var baseline = original.Clone();

        var stored = repository.Update(settings => OllamaModelDownload.ApplyChoice(settings, "gemma4:12b"));
        OllamaModelDownload.CopyChoice(stored, draft);
        OllamaModelDownload.CopyChoice(stored, baseline);
        var reloaded = repository.Load();

        Assert.Equal(LocalServerApp.Ollama, CustomServiceFields.SavedApp(reloaded));
        Assert.Equal("gemma4:12b", reloaded.AiCleanupCustomModel);
        Assert.Null(reloaded.AiCleanupCustomApiKey);
        Assert.Equal("synthetic-current-service-key", reloaded.AiCleanupOtherServiceApiKey);
        Assert.DoesNotContain("synthetic-", repository.Get("app_settings"), StringComparison.Ordinal);
        Assert.Equal("saved writing style", stored.AiCleanupWritingStyle);
        Assert.Equal(90, stored.HistoryRetentionDays);
        Assert.False(stored.EnableAiCleanup);
        Assert.Equal(32768, stored.AiCleanupOllamaContextTokens);
        Assert.Equal(endpoint, stored.AiCleanupOtherServiceEndpoint);
        Assert.Equal("saved-service-model", stored.AiCleanupOtherServiceModel);
        Assert.Equal("synthetic-current-service-key", stored.AiCleanupOtherServiceApiKey);
        Assert.Equal(rememberedApi, stored.AiCleanupOtherServiceApiStyle);
        Assert.Equal("unsaved writing style", draft.AiCleanupWritingStyle);
        Assert.Equal(7, draft.HistoryRetentionDays);
        Assert.True(draft.EnableAiCleanup);
        Assert.Equal("gemma4:12b", draft.AiCleanupCustomModel);
        Assert.Null(draft.AiCleanupCustomApiKey);
        Assert.Equal("synthetic-current-service-key", draft.AiCleanupOtherServiceApiKey);
        Assert.Null(baseline.AiCleanupCustomApiKey);
        Assert.Equal("synthetic-current-service-key", baseline.AiCleanupOtherServiceApiKey);
    }

    [Fact]
    public void An_existing_Ollama_address_and_remembered_service_stay_as_saved()
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(database);
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        settings.AiCleanupCustomEndpoint = "http://127.0.0.1:11434/v1/";
        settings.AiCleanupCustomModel = "gemma4:e4b";
        settings.AiCleanupOtherServiceEndpoint = "https://example.invalid/v1";
        settings.AiCleanupOtherServiceModel = "remembered-model";
        settings.AiCleanupOtherServiceApiKey = "synthetic-remembered-service-key";
        repository.Save(settings);
        var draft = settings.Clone();
        draft.AiCleanupOtherServiceApiKey = "synthetic-outdated-draft-key";

        var stored = repository.Update(value => OllamaModelDownload.ApplyChoice(value, "gemma4:12b"));
        OllamaModelDownload.CopyChoice(stored, draft);
        var reloaded = repository.Load();

        Assert.Equal("http://127.0.0.1:11434/v1/", reloaded.AiCleanupCustomEndpoint);
        Assert.Equal("gemma4:12b", reloaded.AiCleanupCustomModel);
        Assert.Equal("remembered-model", reloaded.AiCleanupOtherServiceModel);
        Assert.Equal("synthetic-remembered-service-key", reloaded.AiCleanupOtherServiceApiKey);
        Assert.Equal("synthetic-remembered-service-key", draft.AiCleanupOtherServiceApiKey);
        Assert.Null(reloaded.AiCleanupCustomApiKey);
    }

    [Theory]
    [InlineData(LocalServerReach.NotRunning)]
    [InlineData(LocalServerReach.Failed)]
    [InlineData(LocalServerReach.NeedsKey)]
    public void A_completion_without_a_reached_model_list_explains_the_refresh_failure(LocalServerReach reach) =>
        Assert.Equal("Downloaded, but Scribe couldn't refresh Ollama's model list. Choose Check again.",
            OllamaModelDownload.CompletionProblem(new(reach, [], []), "gemma4"));

    [Fact]
    public void A_completion_checks_the_returned_reading_and_uses_the_existing_model_name_equivalence()
    {
        var reading = new LocalServerState(LocalServerReach.Reached, [new("gemma4:latest", "Gemma", 1)], []);
        var status = LocalAppSetup.Describe(LocalServerApp.Ollama, reading, "earlier-missing-model", 10);
        Assert.Equal(AiCleanupActionId.CheckAgain, status.Primary!.Id);
        LocalServerState? display = reading;
        display = null; // A concurrent Check again clears the display while the completed read still awaits service status.

        Assert.Null(OllamaModelDownload.CompletionProblem(reading, "gemma4"));
        Assert.NotNull(OllamaModelDownload.CompletionProblem(display, "gemma4"));
        Assert.Equal(
            "Downloaded, but Ollama doesn't list it as a model for AI cleanup. Choose a text model from the catalog.",
            OllamaModelDownload.CompletionProblem(reading, "other-model"));
        Assert.NotNull(OllamaModelDownload.CompletionProblem(null, "gemma4"));
    }

    [Fact]
    public void Switching_never_overwrites_an_unreadable_settings_document()
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(database);
        repository.Set("app_settings", "not a settings document");

        Assert.Throws<InvalidOperationException>(
            () => repository.Update(settings => OllamaModelDownload.ApplyChoice(settings, "gemma4:12b")));

        Assert.Equal("not a settings document", repository.Get("app_settings"));
    }
}
