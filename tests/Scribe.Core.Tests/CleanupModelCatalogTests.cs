using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the curated catalog metadata that the settings UI surfaces. The recommendation is pinned to
/// the local benchmark's best balance in docs/local-model-benchmark.md, so a stray edit that
/// mislabels a model (or drops the default from the list) fails here rather than in the UI.
/// </summary>
public sealed class CleanupModelCatalogTests
{
    [Theory]
    [InlineData("CPU", "CPUExecutionProvider", "CPU")]
    [InlineData("GPU", "CUDAExecutionProvider", "GPU")]
    [InlineData("GPU", "WebGpuExecutionProvider", "GPU")]
    [InlineData("NPU", "QNNExecutionProvider", "NPU")]
    [InlineData("NPU", "VitisAIExecutionProvider", "NPU")]
    public void Describe_reports_the_device_type_the_sdk_supplied(
        string deviceType, string provider, string expected)
    {
        Assert.Contains(expected, FoundryExecutionProviders.Describe(deviceType, provider));
    }

    [Fact]
    public void Describe_reports_a_device_type_it_does_not_recognise_verbatim()
    {
        // The SDK naming a device means it is real. Under WinML the provider set is extended by
        // Windows Update, so silently dropping an unfamiliar one would hide working hardware.
        var text = FoundryExecutionProviders.Describe("TPU", "SomeFutureExecutionProvider");

        Assert.Contains("TPU", text);
        Assert.Contains("SomeFuture", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid")]
    public void Describe_stays_silent_when_the_sdk_reports_no_usable_device(string? deviceType)
    {
        // "Invalid" is the SDK's own value for no meaningful device, so it must not read as a
        // hardware claim.
        Assert.Null(FoundryExecutionProviders.Describe(deviceType, "CPUExecutionProvider"));
    }

    [Fact]
    public void Describe_still_names_the_device_when_no_provider_is_supplied()
    {
        var text = FoundryExecutionProviders.Describe("GPU", null);

        Assert.Contains("GPU", text);
    }

    [Fact]
    public void Model_option_describes_the_hardware_reported_by_the_sdk()
    {
        var npu = new FoundryModelOption(
            "qwen3-1.7b", Cached: true, Loaded: true, "QNNExecutionProvider", "NPU");

        Assert.Contains("NPU", npu.ExecutionBuildLabel);
    }

    [Fact]
    public void Model_option_stays_silent_when_the_sdk_reports_no_device()
    {
        var unknown = new FoundryModelOption("qwen3-1.7b", Cached: true, Loaded: false);

        Assert.Null(unknown.ExecutionBuildLabel);
    }

    [Fact]
    public void Default_alias_is_present_in_the_curated_list()
    {
        Assert.Contains(CleanupModelCatalog.Curated,
            m => string.Equals(m.Alias, CleanupModelCatalog.DefaultAlias, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_curated_model_is_labelled_as_recommended()
    {
        // Settings names no model as the one to pick; the benchmark ranks them (docs/local-model-benchmark.md).
        Assert.DoesNotContain(CleanupModelCatalog.Curated, m =>
            m.DisplayName.Contains("recommend", System.StringComparison.OrdinalIgnoreCase) ||
            m.Hint.Contains("recommend", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_default_is_first_and_the_smallest_quick_model()
    {
        // Resolve(null) returns the first curated model, which must be the default.
        Assert.Equal(CleanupModelCatalog.DefaultAlias, CleanupModelCatalog.Curated[0].Alias);
        Assert.Equal("qwen2.5-1.5b", CleanupModelCatalog.DefaultAlias);
        Assert.Equal("Qwen2.5 1.5B", CleanupModelCatalog.Resolve(null).DisplayName);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", 16L, "qwen2.5-7b")]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 8L, "qwen2.5-7b")]
    [InlineData("NVIDIA GeForce RTX 3050", 6L, "qwen2.5-1.5b")]
    [InlineData("NVIDIA GeForce GTX 1080", 8L, "qwen2.5-1.5b")]
    [InlineData("AMD Radeon RX 7900 XTX", 24L, "qwen2.5-1.5b")]
    [InlineData("AMD Radeon(TM) Graphics", 2L, "qwen2.5-1.5b")]
    public void The_first_setup_starts_from_the_model_this_PC_s_graphics_card_runs_well(string adapter, long gib, string expected)
    {
        // Only an NVIDIA RTX card runs Foundry Local's Qwen2.5 7B on the graphics card (TensorRT for RTX), and it needs
        // room for it; anywhere else it would run on the processor, several times slower than Qwen2.5 1.5B.
        var adapters = new[] { new Scribe.Core.Diagnostics.GraphicsAdapter(adapter, gib * 1024 * 1024 * 1024) };
        Assert.Equal(expected, CleanupModelCatalog.DefaultAliasFor(adapters));
        Assert.Contains(CleanupModelCatalog.Curated, m => m.Alias == expected);
    }

    [Fact]
    public void A_PC_without_a_readable_graphics_card_starts_from_the_default()
    {
        Assert.Equal(CleanupModelCatalog.DefaultAlias, CleanupModelCatalog.DefaultAliasFor([]));
        Assert.All(Scribe.Core.Diagnostics.GraphicsAdapters.Detect(), adapter => Assert.False(string.IsNullOrWhiteSpace(adapter.Name)));
    }

    [Fact]
    public void Models_the_local_benchmark_could_not_run_on_a_gpu_are_no_longer_curated()
    {
        // Measured with Foundry Local 1.2.4 on an RTX 5080 (docs/local-model-benchmark.md): Mistral NeMo 12B's only GPU
        // build failed to start and Phi-4's TensorRT-RTX engine failed to load, so both fell back to the CPU with a 7 to
        // 10 GB download. With 2.1.0 every build of Qwen3.5 2B fails ("Invalid rank for input: position_ids"). They stay
        // available from the live catalog.
        Assert.DoesNotContain(CleanupModelCatalog.Curated, m => m.Alias is "mistral-nemo-12b-instruct" or "phi-4" or "qwen3.5-2b-text");
    }

    [Fact]
    public void Resolve_returns_the_curated_descriptor()
    {
        var model = CleanupModelCatalog.Resolve("QWEN2.5-1.5B");

        Assert.Equal("qwen2.5-1.5b", model.Alias);
        Assert.Equal("Qwen2.5 1.5B", model.DisplayName);
    }

    [Fact]
    public void Resolve_of_an_unknown_alias_describes_it_as_a_custom_model()
    {
        var model = CleanupModelCatalog.Resolve("some-uncurated-model");

        Assert.Equal("some-uncurated-model", model.Alias);
        Assert.Equal("Custom Foundry Local model.", model.Hint);
    }
}
