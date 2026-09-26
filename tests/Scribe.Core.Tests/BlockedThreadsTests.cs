using System.Runtime.CompilerServices;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// Nothing the work of a <see cref="BlockedThreads"/> thread throws leaves its thread, where it would end the test host and
/// hide the failure: it is kept, and reaches the test's thread at the next join, or at a wait for the thread to block that
/// finds it ended instead (stream TR round 6b).
/// </summary>
public sealed class BlockedThreadsTests
{
    [Fact]
    public void A_join_rethrows_what_the_work_threw_with_the_work_s_own_stack()
    {
        var thrown = new InvalidOperationException("The work failed.");
        var thread = BlockedThreads.Start(() => FailInTheWork(thrown));

        var surfaced = Assert.Throws<InvalidOperationException>(() => BlockedThreads.Join(thread));

        Assert.Same(thrown, surfaced);
        Assert.Contains(nameof(FailInTheWork), surfaced.StackTrace); // thrown there, on the helper's thread
    }

    [Fact]
    public void What_the_work_threw_with_nothing_waiting_for_it_is_kept_until_a_join_asks()
    {
        // As a thread that outlived its join does: it throws with no join of the helper's waiting, and the host lives on.
        var thrown = new InvalidOperationException("The work failed with nobody waiting.");
        var thread = BlockedThreads.Start(() => throw thrown);
        Assert.True(thread.Join(BlockedThreads.SafetyTimeout));

        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => BlockedThreads.Join(thread)));
    }

    [Fact]
    public void A_wait_for_a_thread_to_block_rethrows_what_its_work_threw_when_it_ended_instead()
    {
        var thrown = new InvalidOperationException("The work failed before it blocked.");
        var thread = BlockedThreads.Start(() => throw thrown);
        Assert.True(thread.Join(BlockedThreads.SafetyTimeout)); // ended, so the wait below finds it gone and never blocked

        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => BlockedThreads.WaitUntilBlocked(thread)));
    }

    [Fact]
    public void A_thread_whose_work_returns_joins_quietly()
    {
        var ran = false;
        var thread = BlockedThreads.Start(() => ran = true);

        BlockedThreads.Join(thread);

        Assert.True(ran);
    }

    // Never inlined, so the frame names where the work threw; a rethrow that dropped the work's stack loses it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailInTheWork(Exception thrown) => throw thrown;
}
