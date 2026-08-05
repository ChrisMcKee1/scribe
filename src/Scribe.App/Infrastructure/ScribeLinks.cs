namespace Scribe.App.Infrastructure;

/// <summary>
/// The public URLs Scribe points people at. They live in one place because the same Store listing
/// is surfaced from the About page and from two tray commands, and a link that drifts between
/// those surfaces sends people to the wrong product page rather than failing visibly.
/// </summary>
internal static class ScribeLinks
{
    public const string Repository = "https://github.com/ChrisMcKee1/scribe";
    public const string PrivacyPolicy = Repository + "/blob/main/PRIVACY.md";
    public const string NewIssue = Repository + "/issues/new";

    /// <summary>Store product id for Scribe AI, the one value the two Store links derive from.</summary>
    public const string StoreProductId = "9N2P0SG059TJ";

    /// <summary>
    /// Opens the listing inside the Store app. Preferred for "open" because it lands on the
    /// install button directly instead of routing through a browser.
    /// </summary>
    public const string StoreProtocol = "ms-windows-store://pdp/?productid=" + StoreProductId;

    /// <summary>
    /// The shareable form. This is what gets copied for other people: a recipient may be on
    /// another device or platform, where the ms-windows-store protocol resolves to nothing.
    /// </summary>
    public const string StoreWeb = "https://apps.microsoft.com/detail/" + StoreProductId + "?hl=en-us&gl=US&ocid=pdpshare";
}
