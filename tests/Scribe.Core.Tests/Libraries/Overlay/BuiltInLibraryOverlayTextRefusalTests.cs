using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// The two refusals of ill-formed text that no earlier overlay test caught on its own (Grok 4.7's notes on O's round 3,
/// contract 9.1 step 8): a row whose review's own values alone are not text, and a row <c>Collect</c> alone must refuse,
/// because every other check it meets would take it.
/// </summary>
public sealed class BuiltInLibraryOverlayTextRefusalTests
{
    private static readonly TermValues Good = T("key", "K");
    private static readonly TermValues Typed = T("key", "K2");

    [Fact]
    public void A_row_whose_review_holds_text_that_is_not_well_formed_in_its_own_values_alone_is_refused_as_text()
    {
        // Everything the row carries is text but the review's own values (O's six cases put the unpaired surrogate in the
        // updated values). The row is not one the overlay could give either, so only the reason tells the text refusal,
        // which comes first, from the check that the row is the overlay's.
        var row = new LibraryRow(
            K("key"), Typed, TermOrigin.Edited, Good, Edited("key", Good, Typed),
            new TermReview(T("key\uD800", "K2"), Good, TermFields.Spoken));

        Action[] commands =
        [
            () => BuiltInOverlay.Edit(row, T("key", "Other")),
            () => BuiltInOverlay.SetEnabled(row, enabled: false),
            () => BuiltInOverlay.RestoreShipped(row),
            () => BuiltInOverlay.ResolveReview(row, TermReviewChoice.KeepMine),
            () => BuiltInOverlay.ResolveReview(row, TermReviewChoice.UseUpdated),
            () => BuiltInOverlay.Collect(Shipped(Good), null, [row]),
        ];
        foreach (var command in commands)
        {
            var refused = Assert.Throws<ArgumentException>(command);
            Assert.Contains("every surrogate in a pair", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Collect_refuses_a_row_whose_entry_holds_text_that_is_not_well_formed_though_the_row_is_the_overlays()
    {
        // The row is exactly what the overlay gives for an entry whose typed Written holds an unpaired surrogate, over a
        // shipped library that is text: it rebuilds as itself, so only Collect's own check of the row keeps the ill-formed
        // value out of the document (the shipped library's check passes, which is where O's combined case failed first).
        var entry = Edited("key", Good, T("key", "K\uD800"));
        var row = new LibraryRow(K("key"), T("key", "K\uD800"), TermOrigin.Edited, Good, entry);

        var refused = Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(Shipped(Good), null, [row]));

        Assert.Equal("rows", refused.ParamName);
        Assert.Contains("every surrogate in a pair", refused.Message, StringComparison.Ordinal);
    }
}
