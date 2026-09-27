using Scribe.Core.Persistence;

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


    public static QuickAddSourceState<TSource> ApplyDeletion<TSource>(
        IEnumerable<TSource> sources,
        TSource? currentSource,
        HistoryDeletion deletion,
        Func<TSource, string> historyText,
        Func<TSource, DateTimeOffset> timestampUtc,
        Func<TSource, long> addedAtRevision,
        bool hasSavableCorrection)
        where TSource : class
    {
        var sourceList = sources.ToList();
        var remaining = sourceList
            .Where(source => !HistoryDeletionNotifier.Covers(deletion, historyText(source), timestampUtc(source), addedAtRevision(source)))
            .ToList();
        var currentRemoved = currentSource is not null &&
            HistoryDeletionNotifier.Covers(deletion, historyText(currentSource), timestampUtc(currentSource), addedAtRevision(currentSource));
        return new QuickAddSourceState<TSource>(
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

public sealed record QuickAddSourceState<TSource>(
    IReadOnlyList<TSource> Sources,
    bool CurrentRemoved,
    bool KeepCorrection,
    string? Message);
