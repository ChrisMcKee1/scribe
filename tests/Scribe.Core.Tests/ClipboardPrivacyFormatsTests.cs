using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

public sealed class ClipboardPrivacyFormatsTests
{
    [Fact]
    public void Contains_history_and_roaming_opt_out_formats()
    {
        Assert.Equal(
            ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"],
            ClipboardPrivacyFormats.Names);
    }
}
