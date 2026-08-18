using System.ClientModel;
using System.ClientModel.Primitives;
using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Regression coverage for the per-dictation cleanup failure message. A Store user spent a week on
/// "AI cleanup error: ClientResultException" with no way forward: the message carried the exception
/// type and nothing else, while the endpoint's own response said exactly what was wrong
/// ("Model '...' is not loaded. Please load the model before getting a ChatClient.") and the HTTP
/// status was sitting unused on the exception. Every case here asserts the message names the fault.
/// </summary>
public sealed class CleanupFailureDiagnosticsTests
{
    private const string NotLoadedBody =
        "{\"error\":{\"message\":\"Failed to handle OpenAI completion: Model 'phi-4-cuda-gpu:1' is not " +
        "loaded. Please load the model before getting a ChatClient.\"," +
        "\"type\":\"invalid_request_error\",\"code\":null}}";

    private const string QuickGeluWebGpuBody =
        "{\"error\":{\"message\":\"Failed to handle OpenAI completion: Non-zero status code returned " +
        "while running QuickGelu node. Name:'/model/layers.0/mlp/act_fn/Mul/QuickGeluFusion/' " +
        "Status Message: Failed to create a WebGPU compute pipeline: [Invalid ShaderModule " +
        "\\\"QuickGelu\\\"] is invalid due to a previous error. - While validating...\"}}";

    /// <summary>
    /// Builds the exception exactly as it arrives in production. Captured from a live Foundry Local
    /// endpoint after the runtime evicted a resident model: the OpenAI client raises a
    /// <see cref="ClientResultException"/> whose message is "HTTP 400 (invalid_request_error: )" followed
    /// by a blank line and the response body, with the body still attached to the raw response.
    /// </summary>
    private static Exception Http(int status, string? body = null) =>
        new ClientResultException(
            $"HTTP {status} (invalid_request_error: )\n\n{body ?? string.Empty}",
            new FakeResponse(status, body ?? string.Empty));

    /// <summary>Minimal transport response so a real <see cref="ClientResultException"/> carries a status.</summary>
    private sealed class FakeResponse(int status, string body) : PipelineResponse
    {
        public override int Status { get; } = status;
        public override string ReasonPhrase => "test";
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString(body);
        protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders();
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            new(Content);
        public override void Dispose() { }
    }

    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        public override bool TryGetValue(string name, out string? value) { value = null; return false; }
        public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
    }

    [Fact]
    public void Failure_message_never_degrades_to_the_bare_exception_type()
    {
        // The exact reported symptom: "AI cleanup error: ClientResultException." and nothing else.
        var message = TextCleanupService.DescribeFailure(Http(400, NotLoadedBody), CleanupProvider.FoundryLocal);

        Assert.NotEqual("AI cleanup error: ClientResultException.", message);
        Assert.DoesNotContain("ClientResultException", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evicted_local_model_tells_the_user_to_load_it_again()
    {
        var message = TextCleanupService.DescribeFailure(Http(400, NotLoadedBody), CleanupProvider.FoundryLocal);

        Assert.Contains("no longer loaded", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eviction_is_detected_through_wrapping_exceptions()
    {
        // The Agent Framework wraps client faults, so a top-level-only check would miss the eviction
        // and skip the reload that makes cleanup self-heal.
        Assert.True(TextCleanupService.IsModelNotLoaded(Http(400, NotLoadedBody)));
        Assert.True(TextCleanupService.IsModelNotLoaded(
            new InvalidOperationException("agent run failed", Http(400, NotLoadedBody))));
        Assert.True(TextCleanupService.IsModelNotLoaded(
            new AggregateException(new Exception("a"), Http(400, NotLoadedBody))));

        Assert.False(TextCleanupService.IsModelNotLoaded(Http(400, "bad request")));
        Assert.False(TextCleanupService.IsModelNotLoaded(null));
    }

    [Fact]
    public void Unclassified_http_failure_still_reports_the_status_and_server_message()
    {
        var message = TextCleanupService.DescribeFailure(
            Http(400, "context length exceeded"), CleanupProvider.OpenAiCompatible);

        Assert.Contains("400", message, StringComparison.Ordinal);
        Assert.Contains("context length exceeded", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Webgpu_shader_server_failure_tells_user_to_pick_cpu_variant()
    {
        var message = TextCleanupService.DescribeFailure(
            Http(500, QuickGeluWebGpuBody), CleanupProvider.FoundryLocal);

        Assert.Contains("cannot run on this GPU", message, StringComparison.Ordinal);
        Assert.Contains("CPU variant", message, StringComparison.Ordinal);
        Assert.Contains("WebGPU compute pipeline", message, StringComparison.Ordinal);
        Assert.DoesNotContain("transient", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Webgpu_shader_failure_message_is_provider_aware()
    {
        var local = TextCleanupService.DescribeFailure(
            Http(500, QuickGeluWebGpuBody), CleanupProvider.FoundryLocal);
        var custom = TextCleanupService.DescribeFailure(
            Http(500, QuickGeluWebGpuBody), CleanupProvider.OpenAiCompatible);

        Assert.Contains("Foundry Local", local, StringComparison.Ordinal);
        Assert.DoesNotContain("Foundry Local", custom, StringComparison.Ordinal);
        Assert.NotEqual(local, custom);
    }

    [Fact]
    public void Ordinary_server_failure_keeps_the_transient_wording()
    {
        var message = TextCleanupService.DescribeFailure(
            Http(500, "upstream temporarily unavailable"), CleanupProvider.FoundryLocal);

        Assert.Contains("server error (500)", message, StringComparison.Ordinal);
        Assert.Contains("usually transient", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webgpu failed while running quickgelu", true)]
    [InlineData("NON-ZERO STATUS CODE RETURNED WHILE RUNNING node, INVALID SHADERMODULE", true)]
    [InlineData("WebGPU failed without the ONNX node context", true)]
    [InlineData("Failed to create a WebGPU compute pipeline", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Gpu_shader_detection_is_case_insensitive_and_null_safe(string? text, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.MentionsGpuShaderIncompatibility(text));
    }

    [Fact]
    public void Server_message_is_unwrapped_from_the_error_envelope_rather_than_shown_as_json()
    {
        // A remote endpoint that reports the same fault gets the endpoint's sentence, not its JSON.
        var message = TextCleanupService.DescribeFailure(
            Http(400, NotLoadedBody), CleanupProvider.OpenAiCompatible);

        Assert.Contains("Please load the model", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_request_error", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Every_http_status_is_named_in_its_message(int status)
    {
        foreach (var provider in new[]
                 {
                     CleanupProvider.FoundryLocal, CleanupProvider.AzureFoundry, CleanupProvider.OpenAiCompatible,
                 })
        {
            var message = TextCleanupService.DescribeFailure(Http(status), provider);

            Assert.DoesNotContain("ClientResultException", message, StringComparison.Ordinal);

            // A response with no body must not leave the sentence trailing an empty gap.
            Assert.Equal(message.TrimEnd(), message);

            // 404 on Foundry Local is described in words rather than by echoing the code, because the
            // fix is to reselect the model; every other case must carry the status the user can search.
            if (!(status == 404 && provider == CleanupProvider.FoundryLocal))
            {
                Assert.Contains(status.ToString(), message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Evicted_model_is_reloaded_once_per_dictation_and_only_on_device()
    {
        Assert.True(TextCleanupService.ShouldAttemptModelReload(
            evicted: true, CleanupProvider.FoundryLocal, alreadyUsed: false));

        // Second chunk of the same dictation: one failed reload must not be retried per chunk.
        Assert.False(TextCleanupService.ShouldAttemptModelReload(
            evicted: true, CleanupProvider.FoundryLocal, alreadyUsed: true));

        // A remote endpoint's model residency is not ours to manage.
        Assert.False(TextCleanupService.ShouldAttemptModelReload(
            evicted: true, CleanupProvider.AzureFoundry, alreadyUsed: false));
        Assert.False(TextCleanupService.ShouldAttemptModelReload(
            evicted: true, CleanupProvider.OpenAiCompatible, alreadyUsed: false));

        // Any other failure (timeout, throttling, bad key) must not trigger a model load.
        Assert.False(TextCleanupService.ShouldAttemptModelReload(
            evicted: false, CleanupProvider.FoundryLocal, alreadyUsed: false));
    }

    [Fact]
    public void Timeouts_keep_their_existing_message()
    {
        Assert.Equal(
            "AI cleanup timed out.",
            TextCleanupService.DescribeFailure(new OperationCanceledException(), CleanupProvider.FoundryLocal));
        Assert.Equal(
            "AI cleanup timed out.",
            TextCleanupService.DescribeFailure(new TimeoutException(), CleanupProvider.AzureFoundry));
    }

    [Fact]
    public void Connectivity_failure_points_at_the_right_component_per_provider()
    {
        var local = TextCleanupService.DescribeFailure(
            new HttpRequestException("connection refused"), CleanupProvider.FoundryLocal);
        Assert.Contains("Foundry Local", local, StringComparison.Ordinal);

        var custom = TextCleanupService.DescribeFailure(
            new HttpRequestException("connection refused"), CleanupProvider.OpenAiCompatible);
        Assert.Contains("endpoint URL", custom, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_message_is_capped_so_a_response_body_cannot_flood_the_log()
    {
        var message = TextCleanupService.DescribeFailure(
            Http(500, new string('x', 5_000)), CleanupProvider.AzureFoundry);

        Assert.True(message.Length < 2_300, $"message was {message.Length} chars");
    }

    [Fact]
    public void Message_is_a_single_line_so_it_fits_a_status_pill_and_one_log_entry()
    {
        // The OpenAI client formats its message as "HTTP 400 (type: code)\n\n<server message>".
        foreach (var ex in new[] { Http(400, NotLoadedBody), Http(500, "boom"), Http(404, "missing") })
        {
            foreach (var provider in new[] { CleanupProvider.FoundryLocal, CleanupProvider.AzureFoundry })
            {
                var message = TextCleanupService.DescribeFailure(ex, provider);
                Assert.DoesNotContain('\n', message);
                Assert.DoesNotContain('\r', message);
            }
        }
    }
}
