using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

// Activity listeners are process-wide; inherited scenarios intentionally reuse the same fixture text.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InsertionFlagMatrixCollection
{
    public const string Name = "Insertion flag matrix";
}

public abstract class InsertionFlagTest
{
    private protected virtual string FlagNames => "";
    private protected PerfFlags Flags => PerfFlags.Parse(FlagNames);
    private protected const string AllFlags =
        PerfFlags.InputTimings + "," + PerfFlags.SnapshotInjectionLayout + "," + PerfFlags.PreciseLocalTypingSettle;
}

[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorClipboardPasteTests_Timings : TextInjectorClipboardPasteTests
{
    private protected override string FlagNames => PerfFlags.InputTimings;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorClipboardPasteTests_Layout : TextInjectorClipboardPasteTests
{
    private protected override string FlagNames => PerfFlags.SnapshotInjectionLayout;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorClipboardPasteTests_Settle : TextInjectorClipboardPasteTests
{
    private protected override string FlagNames => PerfFlags.PreciseLocalTypingSettle;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorClipboardPasteTests_AllFlags : TextInjectorClipboardPasteTests
{
    private protected override string FlagNames => AllFlags;
}

[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TypingPaceTests_Timings : TypingPaceTests
{
    private protected override string FlagNames => PerfFlags.InputTimings;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TypingPaceTests_Layout : TypingPaceTests
{
    private protected override string FlagNames => PerfFlags.SnapshotInjectionLayout;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TypingPaceTests_Settle : TypingPaceTests
{
    private protected override string FlagNames => PerfFlags.PreciseLocalTypingSettle;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TypingPaceTests_AllFlags : TypingPaceTests
{
    private protected override string FlagNames => AllFlags;
}

[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorUnicodeChunkTests_Timings : TextInjectorUnicodeChunkTests
{
    private protected override string FlagNames => PerfFlags.InputTimings;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorUnicodeChunkTests_Layout : TextInjectorUnicodeChunkTests
{
    private protected override string FlagNames => PerfFlags.SnapshotInjectionLayout;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorUnicodeChunkTests_Settle : TextInjectorUnicodeChunkTests
{
    private protected override string FlagNames => PerfFlags.PreciseLocalTypingSettle;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class TextInjectorUnicodeChunkTests_AllFlags : TextInjectorUnicodeChunkTests
{
    private protected override string FlagNames => AllFlags;
}

[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class InjectedScanCodeTests_Timings : InjectedScanCodeTests
{
    private protected override string FlagNames => PerfFlags.InputTimings;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class InjectedScanCodeTests_Layout : InjectedScanCodeTests
{
    private protected override string FlagNames => PerfFlags.SnapshotInjectionLayout;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class InjectedScanCodeTests_Settle : InjectedScanCodeTests
{
    private protected override string FlagNames => PerfFlags.PreciseLocalTypingSettle;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class InjectedScanCodeTests_AllFlags : InjectedScanCodeTests
{
    private protected override string FlagNames => AllFlags;
}

[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class DictationInsertionTests_Timings : DictationInsertionTests
{
    private protected override string FlagNames => PerfFlags.InputTimings;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class DictationInsertionTests_Layout : DictationInsertionTests
{
    private protected override string FlagNames => PerfFlags.SnapshotInjectionLayout;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class DictationInsertionTests_Settle : DictationInsertionTests
{
    private protected override string FlagNames => PerfFlags.PreciseLocalTypingSettle;
}
[Collection(InsertionFlagMatrixCollection.Name)]
public sealed class DictationInsertionTests_AllFlags : DictationInsertionTests
{
    private protected override string FlagNames => AllFlags;
}
