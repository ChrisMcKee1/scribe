namespace Scribe.Core.Settings;

public sealed record QuickAddSourceState(
    IReadOnlyList<string> Sources,
    bool CurrentRemoved,
    bool KeepCorrection,
    string? Message);

public static class QuickAddSources
{
    public const string RemovedMessage = "That dictation is no longer in the tray's recent list. You can still save the word.";

    public static QuickAddSourceState Forget(
        IEnumerable<string> sources,
        string? currentOriginal,
        string? deletedOriginal,
        bool hasSavableCorrection)
    {
        var remaining = sources
            .Where(source => !string.Equals(source, deletedOriginal, StringComparison.Ordinal))
            .ToList();
        var currentRemoved = !string.IsNullOrWhiteSpace(currentOriginal) &&
            string.Equals(currentOriginal, deletedOriginal, StringComparison.Ordinal);
        return new QuickAddSourceState(
            remaining,
            currentRemoved,
            currentRemoved && hasSavableCorrection,
            currentRemoved && hasSavableCorrection ? RemovedMessage : null);
    }

    public static QuickAddSourceState Clear(bool hasSavableCorrection) =>
        new([], CurrentRemoved: true, KeepCorrection: hasSavableCorrection, hasSavableCorrection ? RemovedMessage : null);

    public static QuickAddSourceState ClearCurrent(bool hasSavableCorrection) =>
        new([], CurrentRemoved: true, KeepCorrection: hasSavableCorrection, hasSavableCorrection ? RemovedMessage : null);
}
