using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Another AI service's API: an address pasted with its API's path is used as it is rather than given the path twice, the
/// API named by the address wins over the one chosen, Ollama and LM Studio at their own addresses stay on Chat Completions,
/// and Responses always says store=false, from Settings to the wire.
/// </summary>
public sealed class CustomApiStyleTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Answer = "Please send the report today.";
    private readonly TempDatabaseFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("https://openrouter.ai/api/v1/chat/completions", CustomApiStyle.ChatCompletions)]
    [InlineData("https://openrouter.ai/api/v1/chat/completions/", CustomApiStyle.ChatCompletions)]
    [InlineData("https://ai.example.invalid/v1/Chat/Completions", CustomApiStyle.ChatCompletions)]
    [InlineData("https://ai.example.invalid/v1/responses", CustomApiStyle.Responses)]
    [InlineData(" https://ai.example.invalid/v1/RESPONSES/ ", CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/responses?api-version=1", CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/v1", null)]
    [InlineData("https://ai.example.invalid/v1/completions", null)]
    [InlineData("https://ai.example.invalid/v1/myresponses", null)]
    [InlineData("not an address", null)]
    [InlineData(null, null)]
    public void An_address_names_the_API_its_path_ends_in(string? address, CustomApiStyle? expected) =>
        Assert.Equal(expected, CustomServiceAddress.NamedStyle(address));

    [Theory]
    [InlineData("https://openrouter.ai/api/v1/chat/completions", "https://openrouter.ai/api/v1")]
    [InlineData("https://ai.example.invalid/v1/responses/", "https://ai.example.invalid/v1")]
    [InlineData("https://ai.example.invalid/v1/chat/completions?api-version=2025-01-01", "https://ai.example.invalid/v1?api-version=2025-01-01")]
    [InlineData("http://localhost:8000/v1/responses", "http://localhost:8000/v1")]
    [InlineData("https://ai.example.invalid/v1", "https://ai.example.invalid/v1")]
    public void Requests_are_built_on_the_address_without_the_API_s_path(string address, string expected) =>
        Assert.Equal(new Uri(expected), CustomServiceAddress.BaseAddress(new Uri(address)));

    [Theory]
    [InlineData("https://ai.example.invalid/v1/completions", true)]
    [InlineData("https://ai.example.invalid/v1/completions/", true)]
    [InlineData("https://ai.example.invalid/v1/chat/completions", false)]
    [InlineData("https://ai.example.invalid/v1", false)]
    [InlineData(null, false)]
    public void Only_the_older_completions_path_is_the_older_API(string? address, bool expected) =>
        Assert.Equal(expected, CustomServiceAddress.NamesOldCompletions(address));

    [Theory]
    [InlineData("https://ai.example.invalid/v1", CustomApiStyle.Responses, CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/v1", CustomApiStyle.ChatCompletions, CustomApiStyle.ChatCompletions)]
    [InlineData("https://ai.example.invalid/v1/responses", CustomApiStyle.ChatCompletions, CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/v1/chat/completions", CustomApiStyle.Responses, CustomApiStyle.ChatCompletions)]
    [InlineData("http://localhost:1234/v1", CustomApiStyle.Responses, CustomApiStyle.ChatCompletions)]
    [InlineData("http://127.0.0.1:11434/v1/", CustomApiStyle.Responses, CustomApiStyle.ChatCompletions)]
    [InlineData("http://localhost:1234/v1/responses", CustomApiStyle.ChatCompletions, CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/v1", (CustomApiStyle)7, CustomApiStyle.ChatCompletions)]
    public void The_address_decides_first_then_the_choice(string address, CustomApiStyle chosen, CustomApiStyle expected) =>
        Assert.Equal(expected, CustomServiceAddress.Effective(CleanupProvider.OpenAiCompatible, address, chosen));

    [Theory]
    [InlineData("https://ai.example.invalid/v1", true)]
    [InlineData("https://ai.example.invalid/v1/responses", false)]
    [InlineData("https://ai.example.invalid/v1/chat/completions", false)]
    [InlineData("http://localhost:1234/v1", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("", true)]
    public void The_API_can_be_chosen_only_when_the_address_leaves_it_open(string address, bool expected) =>
        Assert.Equal(expected, CustomApiStyleText.CanChoose(address));

    [Fact]
    public void Settings_says_why_the_API_is_what_it_is()
    {
        Assert.Contains("ends in /responses, so Scribe uses Responses", CustomApiStyleText.Hint("https://ai.example.invalid/v1/responses"), StringComparison.Ordinal);
        Assert.Contains("ends in /chat/completions, so Scribe uses Chat Completions", CustomApiStyleText.Hint("https://ai.example.invalid/v1/chat/completions"), StringComparison.Ordinal);
        Assert.Equal("LM Studio at its own address uses Chat Completions.", CustomApiStyleText.Hint("http://localhost:1234/v1"));
        Assert.Equal("Ollama at its own address uses Chat Completions.", CustomApiStyleText.Hint("http://localhost:11434/v1"));
        Assert.Contains("Most services take Chat Completions", CustomApiStyleText.Hint("https://ai.example.invalid/v1"), StringComparison.Ordinal);

        // Where Responses can be in use, what Scribe asks of it is said.
        Assert.Contains(CustomApiStyleText.ResponsesStoreNotice, CustomApiStyleText.Hint("https://ai.example.invalid/v1"), StringComparison.Ordinal);
        Assert.Contains(CustomApiStyleText.ResponsesStoreNotice, CustomApiStyleText.Hint("https://ai.example.invalid/v1/responses"), StringComparison.Ordinal);
        Assert.Equal(["Chat Completions", "Responses"], CustomApiStyleText.Choices.Select(CustomApiStyleText.NameOf));
    }

    [Theory]
    [InlineData(CustomApiStyle.ChatCompletions)]
    [InlineData(CustomApiStyle.Responses)]
    public void The_API_round_trips_as_a_name(CustomApiStyle style)
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupCustomApiStyle = style;
        settings.AiCleanupOtherServiceApiStyle = style;

        repository.Save(settings);

        var loaded = repository.Load();
        Assert.Equal(style, loaded.AiCleanupCustomApiStyle);
        Assert.Equal(style, loaded.AiCleanupOtherServiceApiStyle);
        Assert.Contains($"\"aiCleanupCustomApiStyle\":\"{style}\"", repository.Get("app_settings"));
    }

    [Theory]
    [InlineData("{}", CustomApiStyle.ChatCompletions)]
    [InlineData("{\"aiCleanupCustomApiStyle\":\"Messages\"}", CustomApiStyle.ChatCompletions)]
    [InlineData("{\"aiCleanupCustomApiStyle\":\"responses\"}", CustomApiStyle.Responses)]
    [InlineData("{\"aiCleanupCustomApiStyle\":\"1\"}", CustomApiStyle.ChatCompletions)]
    [InlineData("{\"aiCleanupCustomApiStyle\":1}", CustomApiStyle.ChatCompletions)]
    [InlineData("{\"aiCleanupCustomApiStyle\":null}", CustomApiStyle.ChatCompletions)]
    [InlineData("{\"aiCleanupCustomApiStyle\":{\"a\":[1]}}", CustomApiStyle.ChatCompletions)]
    [InlineData("{\"aiCleanupCustomApiStyle\":[]}", CustomApiStyle.ChatCompletions)]
    public void A_missing_or_unknown_API_reads_as_Chat_Completions_without_failing_the_load(string json, CustomApiStyle expected)
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);

        repository.Set("app_settings", json);

        Assert.Equal(expected, repository.Load().AiCleanupCustomApiStyle);
        Assert.False(repository.LastLoadFailed);
    }

    [Fact]
    public void The_API_is_stored_and_remembered_with_another_AI_service()
    {
        var saved = Saved("https://openrouter.ai/api/v1", CustomApiStyle.Responses);
        var boxes = CustomServiceFields.OtherService(saved);
        Assert.Equal(CustomApiStyle.Responses, boxes.ApiStyle);

        // Choosing Ollama stores it with Chat Completions and remembers the service's API with its boxes.
        var (stored, remembered) = CustomServiceFields.ForSave(LocalServerApp.Ollama, "gemma4:e4b", boxes, saved);
        Assert.Equal(CustomApiStyle.ChatCompletions, stored.ApiStyle);
        Assert.Equal(CustomApiStyle.Responses, remembered.ApiStyle);

        // Saved that way, the boxes show it again.
        var next = Saved(stored.Endpoint!, stored.ApiStyle);
        next.AiCleanupCustomModel = stored.Model;
        next.AiCleanupOtherServiceEndpoint = remembered.Endpoint;
        next.AiCleanupOtherServiceModel = remembered.Model;
        next.AiCleanupOtherServiceApiStyle = remembered.ApiStyle;
        Assert.Equal(CustomApiStyle.Responses, CustomServiceFields.OtherService(next).ApiStyle);
    }

    [Theory]
    [InlineData("https://ai.example.invalid/v1/responses", CustomApiStyle.ChatCompletions, CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/v1/chat/completions", CustomApiStyle.Responses, CustomApiStyle.ChatCompletions)]
    [InlineData("https://ai.example.invalid/v1", CustomApiStyle.Responses, CustomApiStyle.Responses)]
    [InlineData("http://localhost:1234/v1", CustomApiStyle.Responses, CustomApiStyle.ChatCompletions)]
    public void Save_stores_the_API_the_boxes_reach_the_service_with(string address, CustomApiStyle chosen, CustomApiStyle expected)
    {
        var (stored, _) = CustomServiceFields.ForSave(
            LocalServerApp.None, null, new(address, "some-model", null, chosen), AppSettings.CreateDefault());

        Assert.Equal(expected, stored.ApiStyle);
        Assert.Equal(address, stored.Endpoint);
    }

    [Fact]
    public async Task A_pasted_Chat_Completions_address_is_sent_to_once_rather_than_given_the_path_twice()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1/chat/completions", "some-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var result = await harness.Service.CleanAsync("please send the report today").WaitAsync(Bound);

        Assert.Equal(Answer, result.Text);
        Assert.All(Snapshot(sent), request =>
        {
            Assert.Equal("/v1/chat/completions", request.Path);
            AssertNoStoreField(request.Body);
        });
    }

    [Theory]
    [InlineData("https://ai.example.invalid/v1", CustomApiStyle.Responses)]
    [InlineData("https://ai.example.invalid/v1/responses", CustomApiStyle.ChatCompletions)]
    [InlineData("https://ai.example.invalid/v1/Responses/", CustomApiStyle.ChatCompletions)]
    public async Task Responses_requests_go_to_responses_and_ask_not_to_be_stored(string address, CustomApiStyle chosen)
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        harness.Service.Configure(CleanupHarness.Custom(address, "some-model") with { CustomApiStyle = chosen });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var result = await harness.Service.CleanAsync("please send the report today").WaitAsync(Bound);

        Assert.Equal(Answer, result.Text);
        var requests = Snapshot(sent);
        Assert.True(requests.Count >= 2, $"Expected the readiness check and the dictation, saw {requests.Count}.");
        Assert.All(requests, request =>
        {
            Assert.Equal("/v1/responses", request.Path);
            AssertStoreFalse(request.Body);
        });
    }

    [Fact]
    public async Task A_one_off_request_to_a_Responses_service_asks_not_to_be_stored_too()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);
        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model") with { CustomApiStyle = CustomApiStyle.Responses });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service
            .CompleteAsync("List the terms.", "we shipped the kubernetes upgrade", harness.Service.Recipient!)
            .WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.Completed, result.Outcome);
        var request = Snapshot(sent)[^1];
        Assert.Equal("/v1/responses", request.Path);
        AssertStoreFalse(request.Body);
    }

    [Fact]
    public async Task Chat_Completions_requests_carry_no_store_field_as_before()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await harness.Service.CleanAsync("please send the report today").WaitAsync(Bound);

        Assert.All(Snapshot(sent), request =>
        {
            Assert.Equal("/v1/chat/completions", request.Path);
            AssertNoStoreField(request.Body);
        });
    }

    [Fact]
    public async Task Ollama_and_LM_Studio_at_their_own_addresses_stay_on_Chat_Completions()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        harness.Service.Configure(CleanupHarness.Custom(LocalAiServer.LmStudioAddress, "google/gemma-4-e2b") with
        {
            CustomApiStyle = CustomApiStyle.Responses,
        });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.All(Snapshot(sent), request => Assert.Equal("/v1/chat/completions", request.Path));
        Assert.NotEmpty(Snapshot(sent));
    }

    [Fact]
    public async Task The_query_of_a_pasted_address_is_kept()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        harness.Service.Configure(CleanupHarness.Custom(
            "https://ai.example.invalid/v1/chat/completions?api-version=2025-01-01", "some-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var request = Assert.Single(Snapshot(sent));
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("?api-version=2025-01-01", request.Query);
    }

    [Fact]
    public async Task Test_connection_reaches_the_service_through_the_API_Save_would_use()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        var result = await harness.Service
            .TestAsync(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model") with { CustomApiStyle = CustomApiStyle.Responses })
            .WaitAsync(Bound);

        Assert.Equal(CleanupTestOutcome.Connected, result.Outcome);
        var request = Assert.Single(Snapshot(sent));
        Assert.Equal("/v1/responses", request.Path);
        AssertStoreFalse(request.Body);
    }

    [Fact]
    public async Task An_address_ending_in_the_older_completions_path_is_refused_with_why()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent);

        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1/completions", "some-model"));
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("the older Completions API, which Scribe doesn't use", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.Empty(Snapshot(sent));
    }

    [Fact]
    public async Task A_service_that_does_not_take_Responses_is_named_as_a_possible_cause()
    {
        var sent = new List<Sent>();
        await using var harness = Recording(sent, path => path.EndsWith("/responses", StringComparison.Ordinal)
            ? ScriptedHttpHandler.Json(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Not Found\"}}")
            : null);

        harness.Service.Configure(CleanupHarness.Custom("https://ai.example.invalid/v1", "some-model") with { CustomApiStyle = CustomApiStyle.Responses });
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        Assert.Contains("doesn't take the Responses API (404)", harness.Service.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("use Chat Completions", harness.Service.StatusDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CustomApiStyle.ChatCompletions, CustomApiStyle.Responses)]
    [InlineData(CustomApiStyle.Responses, CustomApiStyle.ChatCompletions)]
    public async Task Plain_requests_learned_on_one_API_leave_the_other_API_s_requests_alone(CustomApiStyle refusing, CustomApiStyle other)
    {
        // A server on this PC, at an address no app owns, whose one API refuses the reasoning field Scribe sends a local
        // server, and whose other API takes it.
        var sent = new List<Sent>();
        var refusals = new List<string>();
        var refusingPath = refusing == CustomApiStyle.Responses ? "/responses" : "/chat/completions";
        await using var harness = new CleanupHarness(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            lock (sent)
            {
                sent.Add(new Sent(path, request.RequestUri.Query, body));
            }

            if (path.EndsWith(refusingPath, StringComparison.Ordinal) && CarriesReasoning(body))
            {
                lock (refusals)
                {
                    refusals.Add(path);
                }

                return ScriptedHttpHandler.Json(
                    System.Net.HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"Unsupported parameter: reasoning\",\"type\":\"invalid_request_error\"}}");
            }

            return path.EndsWith("/responses", StringComparison.Ordinal) ? ResponsesAnswer(Answer) : ScriptedHttpHandler.ChatCompletion(Answer);
        }));
        const string Address = "http://localhost:8000/v1";
        harness.Service.Configure(CleanupHarness.Custom(Address, "some-model") with { CustomApiStyle = refusing });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The refusing API was refused once, then answered a plain request.
        Assert.Single(Snapshot(refusals));
        Assert.False(CarriesReasoning(Snapshot(sent)[^1].Body), "The fallback did not send a plain request.");

        harness.Service.Configure(CleanupHarness.Custom(Address, "some-model") with { CustomApiStyle = other });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var otherPath = other == CustomApiStyle.Responses ? "/v1/responses" : "/v1/chat/completions";
        var onOther = Snapshot(sent).Where(request => request.Path == otherPath).ToList();
        Assert.NotEmpty(onOther);
        Assert.All(onOther, request => Assert.True(CarriesReasoning(request.Body), $"The other API was sent a plain request: {request.Body}"));
    }

    // The reasoning field Scribe sends a server on this PC: reasoning_effort on Chat Completions, reasoning on Responses.
    private static bool CarriesReasoning(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("reasoning_effort", out _) || document.RootElement.TryGetProperty("reasoning", out _);
    }

    [Fact]
    public void The_privacy_policy_and_the_readme_say_what_Scribe_asks_of_a_Responses_service()
    {
        var policy = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "PRIVACY.md")), @"\s+", " ");
        var readme = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")), @"\s+", " ");

        Assert.Contains(
            "For another AI service you set up to use the Responses API, Scribe asks it not to store responses too; with " +
            "Chat Completions, Scribe never asks it to store anything.",
            policy,
            StringComparison.Ordinal);
        Assert.Contains("With Responses, Scribe asks the service not to store responses.", readme, StringComparison.Ordinal);
        Assert.Contains("an address that ends in `/chat/completions` or `/responses` is used as it is", readme, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scribe.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed record Sent(string Path, string Query, string Body);

    private static AppSettings Saved(string endpoint, CustomApiStyle style)
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        settings.AiCleanupCustomEndpoint = endpoint;
        settings.AiCleanupCustomModel = "some-model";
        settings.AiCleanupCustomApiStyle = style;
        return settings;
    }

    // Every request recorded, answered on the API its path names unless answer says otherwise.
    private static CleanupHarness Recording(List<Sent> sent, Func<string, HttpResponseMessage?>? answer = null) =>
        new(http: new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            lock (sent)
            {
                sent.Add(new Sent(path, request.RequestUri.Query, body));
            }

            return answer?.Invoke(path) ?? (path.EndsWith("/responses", StringComparison.Ordinal)
                ? ResponsesAnswer(Answer)
                : ScriptedHttpHandler.ChatCompletion(Answer));
        }));

    // A minimal Responses API answer carrying one output message.
    private static HttpResponseMessage ResponsesAnswer(string text) => ScriptedHttpHandler.Json(
        System.Net.HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    private static void AssertStoreFalse(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("store", out var store), $"No \"store\" field was sent: {body}");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }

    private static void AssertNoStoreField(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("store", out _), $"A chat completion carried a \"store\" field: {body}");
    }

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list)
        {
            return [.. list];
        }
    }
}
