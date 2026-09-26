namespace Scribe.Core.TextInjection;

public static class ClipboardPrivacyFormats
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];
}
