using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddSelectionTests
{
    [Fact]
    public void Reconcile_keeps_selection_when_box_text_still_matches()
    {
        var range = new QuickDictionaryAdd.WordRange(1, 2);
        Assert.Equal(range, QuickAddSelection.Reconcile(" cloud pilot ", "cloud pilot", range));
    }

    [Fact]
    public void Reconcile_clears_selection_when_typed_text_differs()
    {
        Assert.True(QuickAddSelection.Reconcile("typed", "selected", new QuickDictionaryAdd.WordRange(1, 2)).IsEmpty);
    }
}
