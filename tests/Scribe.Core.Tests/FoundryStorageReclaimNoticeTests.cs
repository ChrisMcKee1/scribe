using System.Globalization;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class FoundryStorageReclaimNoticeTests
{
    private const long Megabyte = 1024L * 1024;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void A_large_reclaim_reads_in_gigabytes_with_the_reason()
    {
        var notice = FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.ProviderIsNotFoundryLocal, (long)(2.3 * 1024 * Megabyte), models: 2),
            Invariant);

        Assert.Equal(
            "Scribe freed 2.3 GB of unused on-device AI files. " +
            "Foundry Local is not your AI cleanup provider, so its downloads are not needed.",
            notice);
    }

    [Fact]
    public void A_smaller_reclaim_reads_in_whole_megabytes()
    {
        var notice = FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.ModelSwitched, 850 * Megabyte, models: 1),
            Invariant);

        Assert.Equal(
            "Scribe freed 850 MB of unused on-device AI files. " +
            "They belonged to Foundry Local models you switched away from.",
            notice);
    }

    [Fact]
    public void The_runtime_without_a_model_says_how_to_get_it_back()
    {
        var notice = FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.RuntimeWithoutModel, 1536 * Megabyte, files: 40),
            Invariant);

        Assert.Equal(
            "Scribe freed 1.5 GB of unused on-device AI files. " +
            "No Foundry Local model is downloaded and AI cleanup is off. " +
            "Setting up Foundry Local downloads the runtime again.",
            notice);
    }

    [Fact]
    public void Runtime_files_left_for_the_next_start_are_mentioned()
    {
        var notice = FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.ProviderIsNotFoundryLocal, 3 * 1024 * Megabyte, models: 1, deferred: true),
            Invariant);

        Assert.NotNull(notice);
        Assert.EndsWith(" Files still in use are removed the next time Scribe starts.", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Less_than_a_megabyte_with_no_model_removed_is_not_worth_a_notice()
    {
        Assert.Null(FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.ProviderIsNotFoundryLocal, 900 * 1024, files: 3), Invariant));
        Assert.Null(FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.RuntimeWithoutModel, 0, files: 1, deferred: true), Invariant));
    }

    [Fact]
    public void A_removed_model_is_mentioned_even_when_its_size_is_unknown()
    {
        var notice = FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.ModelSwitched, 0, models: 1), Invariant);

        Assert.Equal(
            "Scribe removed unused on-device AI files. They belonged to Foundry Local models you switched away from.",
            notice);
    }

    [Fact]
    public void The_size_follows_the_users_culture()
    {
        var notice = FoundryStorageReclaimNotice.Format(
            Reclaim(FoundryStorageReclaimReason.ModelSwitched, (long)(2.3 * 1024 * Megabyte), models: 1),
            CultureInfo.GetCultureInfo("de-DE"));

        Assert.StartsWith("Scribe freed 2,3 GB of unused on-device AI files.", notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(999L * 1024 * 1024, "999 MB")]
    [InlineData(1000L * 1024 * 1024, "1 GB")]
    [InlineData(10L * 1024 * 1024 * 1024, "10 GB")]
    [InlineData(1024L * 1024 - 1, null)]
    [InlineData(0L, null)]
    [InlineData(-5L, null)]
    public void Sizes_move_to_gigabytes_past_a_thousand_megabytes(long bytes, string? expected)
    {
        Assert.Equal(expected, FoundryStorageReclaimNotice.FormatSize(bytes, Invariant));
    }

    [Fact]
    public void Every_notice_fits_a_tray_balloon_and_keeps_to_sizes_and_reasons()
    {
        var shown = 0;
        foreach (var reason in Enum.GetValues<FoundryStorageReclaimReason>())
        {
            foreach (var bytes in new[] { 0L, 5 * Megabyte, 999 * Megabyte, 123L * 1024 * 1024 * 1024 })
            {
                foreach (var deferred in new[] { false, true })
                {
                    var notice = FoundryStorageReclaimNotice.Format(
                        Reclaim(reason, bytes, models: 1, files: 12, deferred: deferred),
                        Invariant);

                    Assert.NotNull(notice);
                    shown++;
                    Assert.True(notice.Length <= FoundryStorageReclaimNotice.MaxLength, notice);
                    Assert.DoesNotContain("\u2014", notice, StringComparison.Ordinal);
                    Assert.DoesNotContain("\u2013", notice, StringComparison.Ordinal);
                    Assert.DoesNotContain("\\", notice, StringComparison.Ordinal);
                    Assert.DoesNotContain("/", notice, StringComparison.Ordinal);
                }
            }
        }

        Assert.Equal(Enum.GetValues<FoundryStorageReclaimReason>().Length * 8, shown);
    }

    [Fact]
    public void A_missing_reclaim_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => FoundryStorageReclaimNotice.Format(null!));
    }

    private static FoundryStorageReclaim Reclaim(
        FoundryStorageReclaimReason reason, long bytes, int models = 0, int files = 0, bool deferred = false) =>
        new(reason, bytes, models, files, deferred);
}
