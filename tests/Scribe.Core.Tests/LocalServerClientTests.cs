using System.Net;
using System.Net.Http;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// What Scribe asks Ollama and LM Studio on this PC, and how it reads their answers: the requests are the apps' own
/// documented ones, shapes taken from Ollama 0.34.4 and 0.35.0 and LM Studio 0.4.25 on the development machine.
/// </summary>
public sealed class LocalServerClientTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1", LocalServerApp.Ollama)]
    [InlineData("http://localhost:11434/v1/", LocalServerApp.Ollama)]
    [InlineData("http://127.0.0.1:11434/v1", LocalServerApp.Ollama)]
    [InlineData("http://[::1]:11434/v1", LocalServerApp.Ollama)]
    [InlineData(" http://LOCALHOST:11434/v1 ", LocalServerApp.Ollama)]
    [InlineData("http://localhost:1234/v1", LocalServerApp.LmStudio)]
    [InlineData("http://127.0.0.1:1234/v1", LocalServerApp.LmStudio)]
    // A port alone does not prove the app: anything but its exact default address is another AI service.
    [InlineData("http://localhost:11434", LocalServerApp.None)]
    [InlineData("http://localhost:11434/v1/chat", LocalServerApp.None)]
    [InlineData("http://localhost:11434/v1?x=1", LocalServerApp.None)]
    [InlineData("https://localhost:11434/v1", LocalServerApp.None)]
    [InlineData("http://localhost:8080/v1", LocalServerApp.None)]
    [InlineData("http://127.0.0.2:11434/v1", LocalServerApp.None)]
    [InlineData("http://ollama.localhost:11434/v1", LocalServerApp.None)]
    [InlineData("http://192.168.1.20:11434/v1", LocalServerApp.None)]
    [InlineData("http://user:pw@localhost:11434/v1", LocalServerApp.None)]
    [InlineData("https://openrouter.ai/api/v1", LocalServerApp.None)]
    [InlineData("", LocalServerApp.None)]
    [InlineData(null, LocalServerApp.None)]
    public void Only_an_app_s_exact_default_address_is_that_app(string? endpoint, LocalServerApp expected)
    {
        Assert.Equal(expected, LocalAiServer.AppAt(endpoint));
        Assert.Equal(
            expected,
            LocalAiServer.AppServing(CleanupProvider.OpenAiCompatible, endpoint));
        Assert.Equal(LocalServerApp.None, LocalAiServer.AppServing(CleanupProvider.AzureFoundry, endpoint));
    }

    [Fact]
    public void The_address_Settings_saves_for_an_app_is_that_app()
    {
        Assert.Equal(LocalServerApp.Ollama, LocalAiServer.AppAt(LocalAiServer.AddressOf(LocalServerApp.Ollama)));
        Assert.Equal(LocalServerApp.LmStudio, LocalAiServer.AppAt(LocalAiServer.AddressOf(LocalServerApp.LmStudio)));
        Assert.Null(LocalAiServer.AddressOf(LocalServerApp.None));
    }

    [Theory]
    [InlineData("gemma4:e2b", "gemma4:e2b", true)]
    [InlineData("gemma3", "gemma3:latest", true)]
    [InlineData("GEMMA3:LATEST", "gemma3", true)]
    [InlineData("library/gemma3", "library/gemma3:latest", true)]
    [InlineData("google/gemma-3-4b", "google/gemma-3-4b", true)]
    [InlineData("gemma3:4b", "gemma3:1b", false)]
    [InlineData("gemma3", "gemma3:4b", false)]
    [InlineData("", "gemma3", false)]
    [InlineData(null, null, false)]
    public void A_name_without_a_tag_is_the_latest_tag(string? first, string? second, bool same) =>
        Assert.Equal(same, LocalServerClient.SameModel(first, second));

    [Fact]
    public async Task Ollama_lists_the_models_it_can_chat_with_and_what_each_loaded_one_takes()
    {
        var requests = new List<string>();
        using var client = new LocalServerClient(new ScriptedHttpHandler((request, _) =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/tags" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[
                      {"name":"qwen3:4b-instruct","size":2500000000,"capabilities":["completion","tools"]},
                      {"name":"nomic-embed-text:latest","size":274000000,"capabilities":["embedding"]},
                      {"name":"gemma4:e2b","size":7200000000,"capabilities":["completion","vision"]},
                      {"name":"old-build:latest","size":1000}
                    ]}
                    """),
                "/api/ps" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[{"name":"gemma4:e2b","model":"gemma4:e2b","size":1706000000,"size_vram":1706000000}]}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }));

        var state = await client.ReadAsync("http://localhost:11434/v1");

        Assert.Equal(LocalServerReach.Reached, state.Reach);
        Assert.Equal(["gemma4:e2b", "old-build:latest", "qwen3:4b-instruct"], state.Models.Select(model => model.Id));
        Assert.Equal(1706000000, state.LoadedFor("gemma4:e2b")!.MemoryBytes);
        Assert.Null(state.LoadedFor("qwen3:4b-instruct"));
        Assert.Equal(["GET /api/tags", "GET /api/ps"], requests);
    }

    [Fact]
    public async Task LM_Studio_lists_only_its_chat_models_and_which_are_loaded()
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/api/v1/models"
                ? ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[
                      {"type":"llm","key":"google/gemma-3-4b","display_name":"Gemma 3 4B","size_bytes":3300000000,
                       "loaded_instances":[{"id":"google/gemma-3-4b","config":{"context_length":8192}}]},
                      {"type":"embedding","key":"text-embedding-nomic","display_name":"Nomic","size_bytes":84000000,"loaded_instances":[]},
                      {"type":"llm","key":"ibm/granite-4-micro","display_name":"Granite 4 Micro","size_bytes":2099555678,"loaded_instances":[]}
                    ]}
                    """)
                : new HttpResponseMessage(HttpStatusCode.NotFound))));

        var state = await client.ReadAsync("http://127.0.0.1:1234/v1");

        Assert.Equal(LocalServerReach.Reached, state.Reach);
        Assert.Equal(["google/gemma-3-4b", "ibm/granite-4-micro"], state.Models.Select(model => model.Id));
        Assert.Equal("Gemma 3 4B", state.Models[0].DisplayName);
        Assert.Equal(3300000000, state.LoadedFor("google/gemma-3-4b")!.MemoryBytes);
        Assert.Null(state.LoadedFor("ibm/granite-4-micro"));
    }

    [Fact]
    public async Task Ollama_frees_a_model_with_keep_alive_zero()
    {
        string? body = null;
        using var client = new LocalServerClient(new ScriptedHttpHandler(async (request, ct) =>
        {
            Assert.Equal("POST /api/generate", $"{request.Method} {request.RequestUri!.AbsolutePath}");
            body = await request.Content!.ReadAsStringAsync(ct);
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"model":"gemma4:e2b","done":true,"done_reason":"unload"}""");
        }));

        Assert.True(await client.UnloadAsync("http://localhost:11434/v1", "gemma4:e2b"));

        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("gemma4:e2b", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, sent.RootElement.GetProperty("keep_alive").GetInt32());
        Assert.False(sent.RootElement.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task LM_Studio_frees_every_instance_of_the_model_and_nothing_else()
    {
        var unloaded = new List<string>();
        using var client = new LocalServerClient(new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[
                      {"type":"llm","key":"qwen/qwen3-4b-2507","loaded_instances":[{"id":"qwen/qwen3-4b-2507"},{"id":"qwen/qwen3-4b-2507:2"}]},
                      {"type":"llm","key":"google/gemma-3-4b","loaded_instances":[{"id":"google/gemma-3-4b"}]}
                    ]}
                    """);
            }

            Assert.Equal("/api/v1/models/unload", request.RequestUri!.AbsolutePath);
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            unloaded.Add(sent.RootElement.GetProperty("instance_id").GetString()!);
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, "{}");
        }));

        Assert.True(await client.UnloadAsync("http://localhost:1234/v1", "qwen/qwen3-4b-2507"));

        Assert.Equal(["qwen/qwen3-4b-2507", "qwen/qwen3-4b-2507:2"], unloaded);
    }

    [Fact]
    public async Task An_app_that_is_not_open_reads_as_not_running()
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made.")));

        Assert.Equal(LocalServerReach.NotRunning, (await client.ReadAsync("http://localhost:11434/v1")).Reach);
        Assert.False(await client.UnloadAsync("http://localhost:11434/v1", "gemma4:e2b"));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 but not JSON")]
    [InlineData("{\"error\":\"unexpected\"}")]
    public async Task An_answer_that_is_not_the_app_s_reads_as_failed_or_empty(string answer)
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, answer))));

        var state = await client.ReadAsync("http://localhost:1234/v1");

        Assert.True(state.Reach == LocalServerReach.Failed || state.Models.Count == 0);
    }

    [Fact]
    public async Task Nothing_is_sent_to_an_address_that_is_not_an_app_on_this_PC()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, "{}")));
        using var client = new LocalServerClient(handler);

        Assert.Equal(LocalServerReach.Failed, (await client.ReadAsync("https://openrouter.ai/api/v1")).Reach);
        Assert.False(await client.UnloadAsync("http://192.168.1.20:11434/v1", "gemma4:e2b"));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task The_key_saved_for_the_address_goes_with_every_request_and_none_without_one()
    {
        var authorizations = new List<string?>();
        using var client = new LocalServerClient(new ScriptedHttpHandler((request, _) =>
        {
            lock (authorizations)
            {
                authorizations.Add(request.Headers.Authorization?.ToString());
            }

            return Task.FromResult(request.Method == HttpMethod.Get
                ? ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[{"type":"llm","key":"google/gemma-4-e2b","loaded_instances":[{"id":"google/gemma-4-e2b"}]}]}
                    """)
                : ScriptedHttpHandler.Json(HttpStatusCode.OK, "{}"));
        }));

        Assert.Equal(LocalServerReach.Reached, (await client.ReadAsync("http://localhost:1234/v1", " lm-token ")).Reach);
        Assert.True(await client.UnloadAsync("http://localhost:1234/v1", "google/gemma-4-e2b", "lm-token"));
        Assert.Equal(LocalServerReach.Reached, (await client.ReadAsync("http://localhost:1234/v1")).Reach);

        Assert.Equal(["Bearer lm-token", "Bearer lm-token", "Bearer lm-token", null], authorizations);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http://localhost:1234/v1")]
    [InlineData(HttpStatusCode.Forbidden, "http://localhost:1234/v1")]
    [InlineData(HttpStatusCode.Unauthorized, "http://localhost:11434/v1")]
    public async Task An_app_that_refuses_Scribe_without_a_key_reads_as_asking_for_one(HttpStatusCode refusal, string endpoint)
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(refusal, """{"error":"Unauthorized"}"""))));

        Assert.Equal(LocalServerReach.NeedsKey, (await client.ReadAsync(endpoint)).Reach);
    }

    [Fact]
    public async Task An_LM_Studio_instance_loaded_under_a_name_of_its_own_is_in_memory_under_that_name()
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                {"models":[{"type":"llm","key":"google/gemma-4-e2b","size_bytes":3000000000,
                  "loaded_instances":[{"id":"my-cleanup-model"}]}]}
                """))));

        var state = await client.ReadAsync("http://localhost:1234/v1");

        Assert.Equal(["google/gemma-4-e2b"], state.Models.Select(model => model.Id));
        Assert.Equal(3000000000, state.LoadedFor("my-cleanup-model")!.MemoryBytes);
        Assert.NotNull(state.LoadedFor("google/gemma-4-e2b"));
    }
}
