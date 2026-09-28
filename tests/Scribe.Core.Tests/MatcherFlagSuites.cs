using Scribe.Core.Diagnostics;
using Xunit.Abstractions;

namespace Scribe.Core.Tests;

// The matcher's suites again with 0.5.1's MatcherPrefilter on (combined.md row 6). Each class inherits every test of its
// base unchanged; only the flags its processors are built with differ, so a test that holds on the old path must hold with
// the prefilter too.
public sealed class PostProcessorTestsWithMatcherPrefilter : PostProcessorTests
{
    protected override PerfFlags MatcherFlags => MatcherFlagSets.Prefilter;
}

public sealed class PostProcessorSourcePassTestsWithMatcherPrefilter : PostProcessorSourcePassTests
{
    protected override PerfFlags MatcherFlags => MatcherFlagSets.Prefilter;
}

public sealed class SnippetTestsWithMatcherPrefilter : SnippetTests
{
    protected override PerfFlags MatcherFlags => MatcherFlagSets.Prefilter;
}

public sealed class LibrarySwitchOffCopyTestsWithMatcherPrefilter(ITestOutputHelper output) : Libraries.LibrarySwitchOffCopyTests(output)
{
    protected override PerfFlags MatcherFlags => MatcherFlagSets.Prefilter;
}

internal static class MatcherFlagSets
{
    public static readonly PerfFlags Prefilter = PerfFlags.Parse(PerfFlags.MatcherPrefilter);
}
