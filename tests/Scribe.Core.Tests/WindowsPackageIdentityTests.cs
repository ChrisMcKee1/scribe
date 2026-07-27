using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public sealed class WindowsPackageIdentityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(122)]
    public void SuccessfulOrSizeProbeResultHasPackageIdentity(int result)
    {
        Assert.True(WindowsPackageIdentity.HasIdentityForProbeResult(result));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(15700)]
    public void ErrorResultDoesNotClaimPackageIdentity(int result)
    {
        Assert.False(WindowsPackageIdentity.HasIdentityForProbeResult(result));
    }
}
