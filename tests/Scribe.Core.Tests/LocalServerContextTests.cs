using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// What Ollama and LM Studio say about the context they loaded a model with, and the requests that load one at a size and
/// free one copy of it. Shapes from Ollama 0.35.0 and LM Studio 0.4.25 on the development machine: Ollama reports a loaded
/// model's <c>context_length</c> in <c>/api/ps</c> and a model's own largest in <c>/api/show</c>; LM Studio reports each
/// instance's <c>config.context_length</c> and <c>remaining_ttl_seconds</c>, and loads at a size through its own chat API.
/// </summary>
public sealed class LocalServerContextTests
{
    [Fact]
    public async Task Ollama_says_what_context_it_loaded_a_model_with()
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"models":[{"name":"gemma4:e4b","size":9600000000}]}"""),
                "/api/ps" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[{"name":"gemma4:e4b","model":"gemma4:e4b","size":3030000000,"size_vram":3030000000,"context_length":32768}]}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            })));

        var state = await client.ReadAsync(LocalAiServer.OllamaAddress);

        Assert.Equal(32768, state.LoadedFor("gemma4:e4b")!.ContextTokens);
        Assert.Null(state.LoadedFor("gemma4:e4b")!.InstanceId);
    }

    [Fact]
    public async Task LM_Studio_says_each_model_s_largest_context_and_each_copy_s_size_and_time_left()
    {
        using var client = new LocalServerClient(new ScriptedHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/v1/models"
                ? ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"models":[
                      {"type":"llm","key":"google/gemma-4-e2b","display_name":"Gemma 4 E2B","size_bytes":4110000000,"max_context_length":131072,
                       "loaded_instances":[{"id":"google/gemma-4-e2b","config":{"context_length":16384,"parallel":4},"remaining_ttl_seconds":3600}]},
                      {"type":"llm","key":"ibm/granite-4-micro","display_name":"Granite","size_bytes":2000000000,"max_context_length":131072,
                       "loaded_instances":[{"id":"ibm/granite-4-micro","config":{"context_length":4096}}]}
                    ]}
                    """)
                : new HttpResponseMessage(HttpStatusCode.NotFound))));

        var state = await client.ReadAsync(LocalAiServer.LmStudioAddress);

        Assert.Equal(131072, state.Models.Single(m => m.Id == "google/gemma-4-e2b").MaxContextTokens);
        var gemma = state.LoadedFor("google/gemma-4-e2b")!;
        Assert.Equal(16384, gemma.ContextTokens);
        Assert.Equal("google/gemma-4-e2b", gemma.InstanceId);
        Assert.Equal(3600, gemma.RemainingTtlSeconds);

        // A copy loaded by hand stays until it is unloaded, so LM Studio gives it no time.
        var granite = state.LoadedFor("ibm/granite-4-micro")!;
        Assert.Equal(4096, granite.ContextTokens);
        Assert.Null(granite.RemainingTtlSeconds);
    }

    [Fact]
    public async Task LM_Studio_loads_a_model_at_a_size_through_its_own_chat_and_keeps_nothing_of_it()
    {
        JsonElement? body = null;
        string? path = null;
        using var client = new LocalServerClient(new ScriptedHttpHandler(async (request, ct) =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                {"model_instance_id":"google/gemma-4-e2b:2","output":[{"type":"message","content":"OK"}],"stats":{"input_tokens":12}}
                """);
        }));

        var instance = await client.LoadWithContextAsync(LocalAiServer.LmStudioAddress, "google/gemma-4-e2b", 32768);

        Assert.Equal("google/gemma-4-e2b:2", instance);
        Assert.Equal("/api/v1/chat", path);
        Assert.Equal("google/gemma-4-e2b", body!.Value.GetProperty("model").GetString());
        Assert.Equal(32768, body.Value.GetProperty("context_length").GetInt32());
        Assert.Equal(1, body.Value.GetProperty("max_output_tokens").GetInt32());
        Assert.False(body.Value.GetProperty("store").GetBoolean());
        Assert.Equal("ok", body.Value.GetProperty("input").GetString());

        // LM Studio refuses keys it does not know, and its own load request a ttl, so neither is sent.
        Assert.False(body.Value.TryGetProperty("ttl", out _));
    }

    [Fact]
    public async Task Loading_at_a_size_is_LM_Studio_s_alone_and_a_refusal_loads_nothing()
    {
        var requests = 0;
        using var client = new LocalServerClient(new ScriptedHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"no"}}"""));
        }));

        Assert.Null(await client.LoadWithContextAsync(LocalAiServer.OllamaAddress, "gemma4:e4b", 32768));
        Assert.Null(await client.LoadWithContextAsync("http://localhost:8080/v1", "x", 32768));
        Assert.Equal(0, requests);
        Assert.Null(await client.LoadWithContextAsync(LocalAiServer.LmStudioAddress, "google/gemma-4-e2b", 0));
        Assert.Null(await client.LoadWithContextAsync(LocalAiServer.LmStudioAddress, "google/gemma-4-e2b", 32768));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task LM_Studio_frees_one_copy_by_its_id()
    {
        JsonElement? body = null;
        string? path = null;
        using var client = new LocalServerClient(new ScriptedHttpHandler(async (request, ct) =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, "{}");
        }));

        Assert.True(await client.UnloadInstanceAsync(LocalAiServer.LmStudioAddress, "google/gemma-4-e2b:2"));
        Assert.Equal("/api/v1/models/unload", path);
        Assert.Equal("google/gemma-4-e2b:2", body!.Value.GetProperty("instance_id").GetString());
        Assert.False(await client.UnloadInstanceAsync(LocalAiServer.OllamaAddress, "gemma4:e4b"));
    }

    [Fact]
    public async Task Ollama_says_the_largest_context_a_model_was_made_for()
    {
        JsonElement? body = null;
        using var client = new LocalServerClient(new ScriptedHttpHandler(async (request, ct) =>
        {
            body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return request.RequestUri!.AbsolutePath == "/api/show"
                ? ScriptedHttpHandler.Json(HttpStatusCode.OK, """
                    {"model_info":{"general.architecture":"gemma3","gemma3.block_count":26,"gemma3.context_length":32768},"capabilities":["completion"]}
                    """)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        Assert.Equal(32768, await client.ReadMaxContextAsync(LocalAiServer.OllamaAddress, "gemma3:1b"));
        Assert.Equal("gemma3:1b", body!.Value.GetProperty("model").GetString());
        Assert.Equal(0, await client.ReadMaxContextAsync(LocalAiServer.LmStudioAddress, "gemma3:1b"));
    }
}
