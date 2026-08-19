using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

public class FoundryExecutionProvidersTests
{
    [Theory]
    [InlineData("NPU", "QNNExecutionProvider")]
    [InlineData("npu", "VitisAIExecutionProvider")]
    public void Describe_reports_the_npu(string device, string provider)
    {
        var text = FoundryExecutionProviders.Describe(device, provider);
        Assert.NotNull(text);
        Assert.Contains("NPU", text);
    }

    [Fact]
    public void Describe_strips_the_execution_provider_suffix()
    {
        Assert.Equal(
            "Runs on the GPU (NvTensorRTRTX).",
            FoundryExecutionProviders.Describe("GPU", "NvTensorRTRTXExecutionProvider"));
    }

    [Fact]
    public void Describe_reports_an_unrecognised_device_verbatim()
    {
        // A device type we have never seen is real because the SDK said so; inventing a category
        // for it or staying silent would both misreport the hardware.
        Assert.Equal("Runs on TPU (Exotic).", FoundryExecutionProviders.Describe("TPU", "ExoticExecutionProvider"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid")]
    public void Describe_stays_silent_without_a_usable_device(string? device)
    {
        Assert.Null(FoundryExecutionProviders.Describe(device, "CPUExecutionProvider"));
    }

    [Theory]
    [InlineData("NPU", "NPU")]
    [InlineData("gpu", "GPU")]
    [InlineData("Cpu", "CPU")]
    public void ShortDevice_normalises_the_devices_the_sdk_names(string device, string expected)
    {
        Assert.Equal(expected, FoundryExecutionProviders.ShortDevice(device));
    }

    [Fact]
    public void ShortDevice_echoes_an_unrecognised_device_unchanged()
    {
        Assert.Equal("TPU", FoundryExecutionProviders.ShortDevice("TPU"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Invalid")]
    public void ShortDevice_stays_silent_without_a_usable_device(string? device)
    {
        Assert.Null(FoundryExecutionProviders.ShortDevice(device));
    }

    [Fact]
    public void DeviceLabel_and_ExecutionBuildLabel_agree_with_the_sdk_device()
    {
        var option = new FoundryModelOption("qwen3-1.7b", true, true, "QNNExecutionProvider", "NPU");
        Assert.Equal("NPU", option.DeviceLabel);
        Assert.Contains("NPU", option.ExecutionBuildLabel);
    }

    [Fact]
    public void DeviceLabel_is_null_when_the_sdk_reports_no_device()
    {
        var option = new FoundryModelOption("qwen3-1.7b", true, false);
        Assert.Null(option.DeviceLabel);
        Assert.Null(option.ExecutionBuildLabel);
    }
}
