using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
#pragma warning disable OPENAI001
using OpenAI.Responses;
#pragma warning restore OPENAI001
using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The AI-cleanup toggle must apply live. The tray quick-toggle and the settings window both reach the
/// engine through <see cref="TextCleanupService.Configure"/> (via DictationController.ApplySettings), so
/// a check/uncheck has to change behavior on the very next dictation with no app restart. These tests
/// pin the engine's half of that contract: <see cref="TextCleanupService.Configure"/> reacts on the
/// calling thread and <see cref="TextCleanupService.CleanAsync"/> honors the new state immediately.
/// </summary>
public sealed class TextCleanupServiceTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("https://example.com/v1", true)]
    [InlineData("http://example.com/v1", false)]
    public void Custom_endpoint_requires_https_except_for_loopback(string value, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.TryValidateCustomEndpoint(value, out _, out _));
    }

    [Theory]
    [InlineData("qwen3-1.7b-generic-gpu", FoundryModelExecutionBuild.Gpu)]
    [InlineData("qwen3-1.7b-generic-cpu", FoundryModelExecutionBuild.Cpu)]
    [InlineData("qwen3-1.7b-cuda-gpu:2", FoundryModelExecutionBuild.Gpu)]
    [InlineData("qwen3-1.7b", FoundryModelExecutionBuild.Unknown)]
    public void Foundry_model_option_exposes_execution_build(string alias, FoundryModelExecutionBuild expected)
    {
        Assert.Equal(expected, FoundryModelVariant.Classify(alias));
    }

    [Fact]
    public void Gpu_alias_demotes_to_catalog_cpu_counterpart()
    {
        var catalogAliases = new[]
        {
            "qwen3-1.7b-generic-gpu",
            "qwen3-1.7b-generic-cpu",
            "mistral-nemo-12b-instruct-generic-cpu",
        };

        var resolved = FoundryModelVariant.ResolveCpuCounterpartAlias(
            "qwen3-1.7b-generic-gpu",
            catalogAliases);

        Assert.Equal("qwen3-1.7b-generic-cpu", resolved);
    }

    [Fact]
    public void Cuda_gpu_alias_demotes_to_generic_cpu_counterpart()
    {
        var catalogAliases = new[]
        {
            "qwen3-1.7b-cuda-gpu:2",
            "qwen3-1.7b-generic-cpu:2",
        };

        var resolved = FoundryModelVariant.ResolveCpuCounterpartAlias(
            "qwen3-1.7b-cuda-gpu:2",
            catalogAliases);

        Assert.Equal("qwen3-1.7b-generic-cpu:2", resolved);
    }

    [Fact]
    public void Cpu_counterpart_resolution_prefers_cpu_execution_provider_metadata()
    {
        var catalogAliases = new[]
        {
            new FoundryModelVariantCandidate("qwen3-1.7b-generic-cpu:2", "WebGpuExecutionProvider"),
            new FoundryModelVariantCandidate("qwen3-1.7b-generic-cpu", "CPUExecutionProvider"),
        };

        var resolved = FoundryModelVariant.ResolveCpuCounterpartAlias(
            "qwen3-1.7b-cuda-gpu:2",
            catalogAliases);

        Assert.Equal("qwen3-1.7b-generic-cpu", resolved);
    }

    [Fact]
    public void Gpu_alias_without_catalog_cpu_counterpart_does_not_demote()
    {
        var resolved = FoundryModelVariant.ResolveCpuCounterpartAlias(
            "qwen3-1.7b-generic-gpu",
            ["qwen3-1.7b-generic-gpu", "phi-4-generic-cpu"]);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("Cannot load model 'qwen3-1.7b-cuda-gpu:2': it requires the 'CUDAExecutionProvider' execution provider, which is not available. Available EPs: [CPUExecutionProvider, WebGpuExecutionProvider].", true)]
    [InlineData("Cannot load model 'qwen3': the file is corrupt.", false)]
    [InlineData("execution provider, which is not available", false)]
    public void Execution_provider_unavailable_detector_is_specific(string message, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.MentionsExecutionProviderUnavailable(message));
    }

    [Fact]
    public void Stored_output_override_creates_responses_options_with_storage_disabled()
    {
        var options = TextCleanupService.WithStoredOutputDisabled(null);

#pragma warning disable OPENAI001
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(new StubChatClient()));

        Assert.False(raw.StoredOutputEnabled);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void Stored_output_override_preserves_existing_responses_options()
    {
        var source = new ChatOptions
        {
#pragma warning disable OPENAI001
            RawRepresentationFactory = _ => new CreateResponseOptions { StoredOutputEnabled = true },
#pragma warning restore OPENAI001
        };

        var options = TextCleanupService.WithStoredOutputDisabled(source);

#pragma warning disable OPENAI001
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(new StubChatClient()));

        Assert.False(raw.StoredOutputEnabled);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void Stored_output_override_fails_closed_on_an_unrecognised_raw_representation()
    {
        // A future package version could start supplying its own factory returning some other type.
        // Passing that through would let Azure apply its store=true default and silently retain
        // dictated text, so the override must replace it rather than defer to it.
        var source = new ChatOptions
        {
            RawRepresentationFactory = _ => new object(),
        };

        var options = TextCleanupService.WithStoredOutputDisabled(source);

#pragma warning disable OPENAI001
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(new StubChatClient()));

        Assert.False(raw.StoredOutputEnabled);
#pragma warning restore OPENAI001
    }

    [Fact]
    public async Task Unchecking_cleanup_disables_it_immediately_without_restart()
    {
        await using var svc = new TextCleanupService(NullLogger<TextCleanupService>.Instance);

        // "Uncheck" the box. Configure with a disabled snapshot flips the status synchronously; there
        // is no background work and no relaunch, so the next dictation passes raw text straight through.
        svc.Configure(CleanupOptions.Disabled);

        Assert.Equal(CleanupStatus.Disabled, svc.Status);

        var result = await svc.CleanAsync("can you hear me now");

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Equal("can you hear me now", result.Text);
    }

    [Fact]
    public async Task Toggling_cleanup_on_then_off_takes_effect_live()
    {
        await using var svc = new TextCleanupService(NullLogger<TextCleanupService>.Instance);

        // "Check" the box. An Azure provider with no endpoint configured yet is enabled but not
        // actionable, so the engine reacts synchronously (leaves Disabled for Unavailable) without
        // spawning a real model or a network call. The point is that it reacts at all: immediately,
        // rather than waiting for a relaunch.
        svc.Configure(CleanupOptions.Disabled with { Enabled = true, Provider = CleanupProvider.AzureFoundry });
        Assert.NotEqual(CleanupStatus.Disabled, svc.Status);

        // Not Ready, so dictation is never blocked: it passes through untouched while enabled-but-not-ready.
        var whileEnabling = await svc.CleanAsync("please book the demo room for thursday");
        Assert.Equal(CleanupOutcome.Skipped, whileEnabling.Outcome);

        // "Uncheck" the box again: back to Disabled synchronously, still passing raw text through.
        svc.Configure(CleanupOptions.Disabled);
        Assert.Equal(CleanupStatus.Disabled, svc.Status);

        var afterDisable = await svc.CleanAsync("please book the demo room for thursday");
        Assert.Equal(CleanupOutcome.Skipped, afterDisable.Outcome);
        Assert.Equal("please book the demo room for thursday", afterDisable.Text);
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
