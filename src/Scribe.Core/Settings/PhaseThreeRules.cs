using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.Core.Settings;

public sealed record RecordingIndicatorState(bool ShowPositionPicker, bool ShowPreview);

public static class RecordingIndicatorRules
{
    public static RecordingIndicatorState Describe(bool showRecordingIndicator) =>
        new(showRecordingIndicator, showRecordingIndicator);
}

public static class ShortcutCaveats
{
    private const uint PageUp = 0x21;
    private const uint PageDown = 0x22;
    private const uint RightShift = 0xA1;
    private const uint NumLock = 0x90;
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;
    private const uint LeftShift = 0xA0;
    private const uint LeftWin = 0x5B;
    private const uint RightWin = 0x5C;

    public static string? For(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return null;
        }

        if (binding.Modifiers == KeyModifiers.None && binding.SecondaryVirtualKey is null)
        {
            return binding.VirtualKey switch
            {
                PageDown => "While Scribe runs, Page Down on its own doesn't reach other apps, so it won't page through documents or change slides. With Ctrl, Shift, Alt or the Windows key it works as usual. On many laptops, Page Down is Fn plus the Down arrow.",
                PageUp => "While Scribe runs, Page Up on its own doesn't reach other apps, so it won't page through documents or change slides. With Ctrl, Shift, Alt or the Windows key it works as usual. On many laptops, Page Up is Fn plus the Up arrow.",
                RightShift => "Holding Right Shift can turn on Filter Keys in Windows. Try a two-key combination instead.",
                NumLock => "Holding Num Lock can turn on Toggle Keys in Windows. Try another key.",
                LeftControl or RightControl or LeftAlt or RightAlt or LeftShift or LeftWin or RightWin =>
                    "If you use Sticky Keys, holding this key can turn them on.",
                _ => null,
            };
        }

        return binding.Modifiers.HasFlag(KeyModifiers.Win) || binding.VirtualKey is LeftWin or RightWin
            ? "This combination replaces a Windows shortcut while Scribe runs."
            : null;
    }
}

public sealed record TranscriptionModelChoice(
    string Id,
    string Label,
    string Hint,
    bool IsSelected,
    bool IsInstalled,
    bool ShowInstall,
    string StatusText);

public static class TranscriptionModelChoices
{
    public static IReadOnlyList<TranscriptionModelChoice> Build(
        string? selectedModelId,
        IReadOnlySet<string> installedModelIds)
    {
        ArgumentNullException.ThrowIfNull(installedModelIds);

        var selected = TranscriptionModelCatalog.Resolve(selectedModelId).Id;
        return TranscriptionModelCatalog.Curated
            .Select(model =>
            {
                var installed = installedModelIds.Contains(model.Id);
                return new TranscriptionModelChoice(
                    model.Id,
                    model.DisplayName,
                    $"{model.Languages}. {FormatSize(model.DownloadSize)}. {(installed ? "Downloaded." : "Choose Install to download.")}",
                    model.Id == selected,
                    installed,
                    ShowInstall: !installed,
                    installed ? "Downloaded" : string.Empty);
            })
            .ToList();
    }

    private static string FormatSize(long bytes)
    {
        var mb = (int)Math.Round(bytes / 1_000_000d);
        return mb >= 1000
            ? $"about {mb / 1000d:0.#} GB"
            : $"{mb} MB";
    }
}
