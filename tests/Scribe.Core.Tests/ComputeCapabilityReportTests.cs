using System.Runtime.InteropServices;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class ComputeCapabilityReportTests
{
    [Fact]
    public void X64OnX64IsNativeAndNeedsNoAdvice()
    {
        var report = ComputeCapabilityReport.Create(Architecture.X64, Architecture.X64);

        Assert.False(report.IsEmulated);
        Assert.False(report.IsArm64Native);
        Assert.Null(report.Recommendation);
    }

    [Fact]
    public void Arm64OnArm64IsNativeAndNeedsNoAdvice()
    {
        var report = ComputeCapabilityReport.Create(Architecture.Arm64, Architecture.Arm64);

        Assert.False(report.IsEmulated);
        Assert.True(report.IsArm64Native);
        Assert.Null(report.Recommendation);
    }

    [Fact]
    public void X64OnArm64IsEmulatedAndRecommendsTheNativeBuild()
    {
        var report = ComputeCapabilityReport.Create(Architecture.X64, Architecture.Arm64);

        Assert.True(report.IsEmulated);
        Assert.False(report.IsArm64Native);
        Assert.Contains("Arm64 build", report.Recommendation);
    }

    [Fact]
    public void MachineWithNoAcceleratorReportsNoNpu()
    {
        var report = ComputeCapabilityReport.Create(Architecture.Arm64, Architecture.Arm64);

        Assert.False(report.HasNpu);
        Assert.Empty(report.Accelerators);
        Assert.Contains("none detected", report.Describe());
    }

    [Fact]
    public void MachineWithAcceleratorReportsIt()
    {
        var report = ComputeCapabilityReport.Create(
            Architecture.Arm64,
            Architecture.Arm64,
            [new NeuralAccelerator("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm)]);

        Assert.True(report.HasNpu);
        Assert.Contains("Hexagon", report.Describe());
    }

    [Fact]
    public void BlankAcceleratorNamesAreDiscardedSoTheyCannotFakeAnNpu()
    {
        var report = ComputeCapabilityReport.Create(
            Architecture.Arm64,
            Architecture.Arm64,
            [new NeuralAccelerator("   ", AcceleratorVendor.Unknown)]);

        Assert.False(report.HasNpu);
    }

    [Fact]
    public void DescribeAlwaysStatesCpuDecodeBecauseThatIsTheOnlyDecodePath()
    {
        var withNpu = ComputeCapabilityReport.Create(
            Architecture.Arm64,
            Architecture.Arm64,
            [new NeuralAccelerator("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm)]);

        Assert.StartsWith("CPU decode", withNpu.Describe());
    }

    [Fact]
    public void DescribeNamesEmulationExplicitly()
    {
        var report = ComputeCapabilityReport.Create(Architecture.X64, Architecture.Arm64);

        Assert.Contains("x64 emulated on Arm64", report.Describe());
    }

    [Theory]
    [InlineData("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm)]
    [InlineData("Snapdragon X Elite NPU", AcceleratorVendor.Qualcomm)]
    [InlineData("Intel(R) AI Boost", AcceleratorVendor.Intel)]
    [InlineData("AMD XDNA accelerator", AcceleratorVendor.Amd)]
    [InlineData("Ryzen AI NPU", AcceleratorVendor.Amd)]
    [InlineData("Some Unlabelled Accelerator", AcceleratorVendor.Unknown)]
    [InlineData("", AcceleratorVendor.Unknown)]
    [InlineData(null, AcceleratorVendor.Unknown)]
    public void VendorIsClassifiedFromTheDeviceName(string? name, AcceleratorVendor expected) =>
        Assert.Equal(expected, ComputeCapabilityReport.ClassifyVendor(name));

    [Fact]
    public void DetectOnThisMachineNeverThrowsAndAgreesWithTheRuntime()
    {
        var report = ComputeCapabilityReport.Detect();

        Assert.Equal(RuntimeInformation.ProcessArchitecture, report.ProcessArchitecture);
        Assert.Equal(RuntimeInformation.OSArchitecture, report.OsArchitecture);
        Assert.False(string.IsNullOrWhiteSpace(report.Describe()));
    }
}
