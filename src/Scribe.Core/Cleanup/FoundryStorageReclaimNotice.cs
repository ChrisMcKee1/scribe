using System.Globalization;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The tray notice for a <see cref="FoundryStorageReclaim"/>: how much disk space Scribe gave back, and why,
/// in the user's terms. Sizes and reasons only, like the reclaim itself: no paths and no model names.
/// </summary>
public static class FoundryStorageReclaimNotice
{
    /// <summary>
    /// The longest text a tray balloon shows; Windows cuts the notification text at 255 characters.
    /// </summary>
    public const int MaxLength = 255;

    private const long Megabyte = 1024L * 1024;

    // Four digits of megabytes read worse than a gigabyte figure, so the unit changes at 1000 MB.
    private const long GigabyteThreshold = 1000 * Megabyte;

    /// <summary>
    /// The notice text, such as "Scribe freed 2.3 GB of unused on-device AI files.", followed by why.
    /// Returns null when the reclaim gave back less than a megabyte and removed no model, which is not worth
    /// interrupting anyone for.
    /// </summary>
    public static string? Format(FoundryStorageReclaim reclaim, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reclaim);
        culture ??= CultureInfo.CurrentCulture;

        var size = FormatSize(reclaim.BytesFreed, culture);
        if (size is null && reclaim.ModelsRemoved <= 0)
        {
            return null;
        }

        var freed = size is null
            ? "Scribe removed unused on-device AI files."
            : $"Scribe freed {size} of unused on-device AI files.";
        var why = reclaim.Reason switch
        {
            FoundryStorageReclaimReason.ProviderIsNotFoundryLocal =>
                "Foundry Local is not your AI cleanup provider, so its downloads are not needed.",
            FoundryStorageReclaimReason.ModelSwitched =>
                "They belonged to Foundry Local models you switched away from.",
            FoundryStorageReclaimReason.RuntimeWithoutModel =>
                "No Foundry Local model is downloaded and AI cleanup is off. Setting up Foundry Local downloads the runtime again.",
            _ => "They were no longer needed.",
        };
        var notice = $"{freed} {why}";
        if (reclaim.RuntimeDeletedAtNextStart)
        {
            notice += " Files still in use are removed the next time Scribe starts.";
        }

        return notice;
    }

    /// <summary>"2.3 GB" or "850 MB", or null below a megabyte.</summary>
    public static string? FormatSize(long bytes, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < Megabyte)
        {
            return null;
        }

        return bytes >= GigabyteThreshold
            ? string.Format(culture, "{0:0.#} GB", bytes / (double)(Megabyte * 1024))
            : string.Format(culture, "{0:0} MB", bytes / (double)Megabyte);
    }
}
