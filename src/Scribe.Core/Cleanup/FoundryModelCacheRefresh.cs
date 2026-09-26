namespace Scribe.Core.Cleanup;

public sealed class FoundryModelCacheRefresh
{
    private long _latest;

    public long Next() => Interlocked.Increment(ref _latest);

    public bool IsLatest(long revision) => revision == Interlocked.Read(ref _latest);
}
