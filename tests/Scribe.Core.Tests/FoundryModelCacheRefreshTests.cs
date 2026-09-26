using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class FoundryModelCacheRefreshTests
{
    [Fact]
    public void Older_cache_scan_cannot_publish_over_newer_request()
    {
        var refresh = new FoundryModelCacheRefresh();
        var first = refresh.Next();
        var second = refresh.Next();

        Assert.False(refresh.IsLatest(first));
        Assert.True(refresh.IsLatest(second));
    }
}
