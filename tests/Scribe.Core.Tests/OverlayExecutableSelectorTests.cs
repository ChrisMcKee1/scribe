using System.Runtime.InteropServices;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public class OverlayExecutableSelectorTests
{
    private const string Arm64Debug =
        @"C:\repo\src\Scribe.Overlay\bin\ARM64\Debug\net10.0-windows10.0.19041.0\win-arm64\Scribe.Overlay.exe";

    private const string Arm64Release =
        @"C:\repo\src\Scribe.Overlay\bin\ARM64\Release\net10.0-windows10.0.19041.0\win-arm64\Scribe.Overlay.exe";

    private const string X64Debug =
        @"C:\repo\src\Scribe.Overlay\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Scribe.Overlay.exe";

    private const string X64Release =
        @"C:\repo\src\Scribe.Overlay\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\Scribe.Overlay.exe";

    [Fact]
    public void Select_PrefersMatchingArchitectureOverAlphabeticalOrder()
    {
        // ARM64 sorts first, which is exactly how the x64 dev build used to launch an ARM64 pill.
        var best = OverlayExecutableSelector.Select(
            [Arm64Debug, Arm64Release, X64Debug, X64Release],
            "Debug",
            Architecture.X64);

        Assert.Equal(X64Debug, best);
    }

    [Fact]
    public void Select_PrefersMatchingArchitectureForArm64Host()
    {
        var best = OverlayExecutableSelector.Select(
            [Arm64Debug, Arm64Release, X64Debug, X64Release],
            "Release",
            Architecture.Arm64);

        Assert.Equal(Arm64Release, best);
    }

    [Fact]
    public void Select_ReturnsNullWhenOnlyMismatchedArchitecturesExist()
    {
        // Better no pill than a guaranteed Win32 216 launch failure on every dictation.
        var best = OverlayExecutableSelector.Select([Arm64Debug, Arm64Release], "Debug", Architecture.X64);

        Assert.Null(best);
    }

    [Fact]
    public void Select_FallsBackToOtherConfigurationOfMatchingArchitecture()
    {
        var best = OverlayExecutableSelector.Select([Arm64Debug, X64Release], "Debug", Architecture.X64);

        Assert.Equal(X64Release, best);
    }

    [Fact]
    public void Select_AcceptsArchitectureLessLayoutAsLastResort()
    {
        const string plain = @"C:\repo\src\Scribe.Overlay\bin\Debug\net10.0-windows10.0.19041.0\Scribe.Overlay.exe";

        var best = OverlayExecutableSelector.Select([plain], "Debug", Architecture.X64);

        Assert.Equal(plain, best);
    }

    [Fact]
    public void Select_PrefersRealArchitectureMatchOverArchitectureLessLayout()
    {
        const string plain = @"C:\repo\src\Scribe.Overlay\bin\Debug\net10.0-windows10.0.19041.0\Scribe.Overlay.exe";

        var best = OverlayExecutableSelector.Select([plain, X64Debug], "Debug", Architecture.X64);

        Assert.Equal(X64Debug, best);
    }

    [Fact]
    public void Select_ReturnsNullForEmptyCandidates()
    {
        Assert.Null(OverlayExecutableSelector.Select([], "Debug", Architecture.X64));
    }

    [Fact]
    public void Select_IsStableRegardlessOfEnumerationOrder()
    {
        var forward = OverlayExecutableSelector.Select([X64Debug, X64Release], "Staging", Architecture.X64);
        var reversed = OverlayExecutableSelector.Select([X64Release, X64Debug], "Staging", Architecture.X64);

        Assert.Equal(forward, reversed);
    }

    [Theory]
    [InlineData(@"C:\a\win-arm64\Scribe.Overlay.exe", Architecture.Arm64)]
    [InlineData(@"C:\a\ARM64\Debug\Scribe.Overlay.exe", Architecture.Arm64)]
    [InlineData(@"C:\a\win-x64\Scribe.Overlay.exe", Architecture.X64)]
    [InlineData(@"C:\a\x64\Release\Scribe.Overlay.exe", Architecture.X64)]
    [InlineData(@"C:\a\win-x86\Scribe.Overlay.exe", Architecture.X86)]
    public void DetectArchitecture_RecognisesPlatformAndRuntimeFolders(string path, Architecture expected)
    {
        Assert.Equal(expected, OverlayExecutableSelector.DetectArchitecture(path));
    }

    [Fact]
    public void DetectArchitecture_PrefersTheRuntimeFolderNearestTheBinary()
    {
        const string path = @"C:\x64\src\Scribe.Overlay\bin\ARM64\Debug\net10.0\win-arm64\Scribe.Overlay.exe";

        Assert.Equal(Architecture.Arm64, OverlayExecutableSelector.DetectArchitecture(path));
    }

    [Fact]
    public void DetectArchitecture_ReturnsNullWhenNoSegmentIdentifiesOne()
    {
        Assert.Null(OverlayExecutableSelector.DetectArchitecture(@"C:\repo\bin\Debug\net10.0\Scribe.Overlay.exe"));
    }
}
