using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the curated catalog metadata that the settings UI surfaces. The recommendation strings
/// are pinned to the golden-suite benchmark winners in docs/model-leaderboard.md, so a stray edit
/// that mislabels a model (or drops a winner from the list) fails here rather than in the UI.
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
    public void Only_the_leaderboard_winners_carry_a_recommendation()
    {
        var recommended = CleanupModelCatalog.Curated
            .Where(m => m.Recommendation is not null)
            .Select(m => m.Alias)
            .ToArray();

        Assert.Equal(new[] { "mistral-nemo-12b-instruct", "phi-4" }, recommended);
    }

    [Theory]
    [InlineData("mistral-nemo-12b-instruct", "Best on-device balance")]
    [InlineData("phi-4", "Best on-device quality")]
    public void Winners_carry_the_expected_recommendation_text(string alias, string recommendation)
    {
        var model = CleanupModelCatalog.Curated.Single(m => m.Alias == alias);
        Assert.Equal(recommendation, model.Recommendation);
    }

    [Fact]
    public void Non_winners_leave_recommendation_null()
    {
        foreach (var model in CleanupModelCatalog.Curated)
        {
            if (model.Alias is "mistral-nemo-12b-instruct" or "phi-4")
            {
                continue;
            }

            Assert.Null(model.Recommendation);
        }
    }

    [Fact]
    public void Resolve_returns_the_curated_descriptor_with_its_recommendation()
    {
        var model = CleanupModelCatalog.Resolve("phi-4");

        Assert.Equal("phi-4", model.Alias);
        Assert.Equal("Best on-device quality", model.Recommendation);
    }

    [Fact]
    public void Resolve_of_an_unknown_alias_has_no_recommendation()
    {
        var model = CleanupModelCatalog.Resolve("some-uncurated-model");

        Assert.Equal("some-uncurated-model", model.Alias);
        Assert.Null(model.Recommendation);
    }
}
