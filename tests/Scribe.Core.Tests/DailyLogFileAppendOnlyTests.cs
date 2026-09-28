using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// Every <see cref="DailyLogFileTests"/> case again with the file appended only (DATA-O-02,
/// <see cref="PerfFlags.AppendOnlyLog"/>): day changes both ways, the scrub's exclusions, budgets and their notice, the
/// fallback folder, a locked file, a deleted folder, redaction and the encoding all behave as they do today.
/// </summary>
public sealed class DailyLogFileAppendOnlyTests : DailyLogFileTests
{
    protected override AppendOnlyLogMode? AppendMode => AppendOnlyLogMode.Fixed(true);
}
