using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Libraries.Composition;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The usage insight as the Settings window makes it once the library service is the vocabulary source: the report is
/// built with the library service (<see cref="UsageReport.Build(IHistoryRepository, IDictionaryRepository, PostProcessing.IDictionaryLibraryService, IReadOnlyCollection{string}, int?, DateTimeOffset, CancellationToken)"/>),
/// its <see cref="UsageReport.Result.LibraryScope"/> is handed to the scoped <c>CompleteAsync</c> with the summary, and
/// the request reaches the network only through the hand-off transport (contract 3.3.6). The library is composed by the
/// real composer, and the service judges narrowing by the real policy.
/// </summary>
public sealed class UsageInsightScopeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // Letters only, so JSON escaping in a request body can never hide it.
    private const string CanaryWritten = "Quillharbor";

    [Fact]
    public async Task A_cached_report_is_never_sent_once_its_librarys_permission_or_content_changed()
    {
        var source = new FakeVocabularySource(Compose(permitted: true, content: H1));
        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var recipient = harness.Service.Recipient!;

        var report = UsageReport.Build(new History(), new VocabularyPublisherTests.ScriptedDictionary([]), source, [], periodDays: null, Now);
        Assert.Contains(report.Snapshot.Terms, term => term is { Text: CanaryWritten, Covered: true, Shareable: true });
        Assert.Equal(["canary"], report.LibraryScope.PermittedLibraryIds);
        var summary = UsageInsight.BuildSummary(report.Snapshot);
        Assert.Contains(CanaryWritten, summary, StringComparison.Ordinal);

        // While the library is permitted with the content the report was built from, the insight goes, label included.
        var sent = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, summary, recipient, report.LibraryScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, sent.Outcome);
        Assert.Single(harness.Network.Sent, request => request.Carries(CanaryWritten));

        // Its permission revoked after the report was built: the cached report is not sent.
        source.Publish(Compose(permitted: false, content: H1));
        var revoked = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, summary, recipient, report.LibraryScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.LibraryScopeNarrowed, revoked.Outcome);
        Assert.True(revoked.NothingSent);

        // Permitted again, but for new content: permission covers what it was given for, so the old report still stays.
        source.Publish(Compose(permitted: true, content: H2));
        var changed = await harness.Service.CompleteAsync(UsageInsight.SystemPrompt, summary, recipient, report.LibraryScope).WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.LibraryScopeNarrowed, changed.Outcome);
        Assert.True(changed.NothingSent);
        Assert.Single(harness.Network.Sent, request => request.Carries(CanaryWritten));

        // A report built from what is published now goes.
        var rebuilt = UsageReport.Build(new History(), new VocabularyPublisherTests.ScriptedDictionary([]), source, [], periodDays: null, Now);
        var again = await harness.Service
            .CompleteAsync(UsageInsight.SystemPrompt, UsageInsight.BuildSummary(rebuilt.Snapshot), recipient, rebuilt.LibraryScope)
            .WaitAsync(Bound);
        Assert.Equal(ScopedCompletionOutcome.Completed, again.Outcome);
        Assert.Equal(2, harness.Network.Sent.Count(request => request.Carries(CanaryWritten)));
    }

    // One custom library whose one term turns up in every dictation below, composed with the given permission and content.
    private static LibraryVocabulary Compose(bool permitted, LibraryContentHash content)
    {
        var canary = CustomLibrary("canary", Custom("quill harbor", CanaryWritten));
        var catalog = Catalog(
            State(enabled: ["canary"], ai: [("canary", permitted)], accepted: [("canary", content)]),
            Committed(canary, content));
        return LibraryComposer.Instance.ComposeVocabulary(catalog);
    }

    private sealed class History : IHistoryRepository
    {
        private static readonly HistoryEntry[] Entries =
        [
            Entry(1, "Quillharbor rollout notes for today"),
            Entry(2, "Quillharbor again, and the rest of the plan"),
            Entry(3, "one more Quillharbor review"),
        ];

        public HistoryEntry Add(HistoryEntry entry) => throw new NotSupportedException();

        public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio) => throw new NotSupportedException();

        public long AddAudioBlob(CapturedAudio audio) => throw new NotSupportedException();

        public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100) => [.. Entries.Take(limit)];

        public CapturedAudio? GetAudio(long blobId) => throw new NotSupportedException();

        public void SetAiRating(long id, AiRating rating) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public int PruneOlderThan(DateTimeOffset cutoffUtc) => throw new NotSupportedException();

        private static HistoryEntry Entry(long id, string text) =>
            new(id, Now.AddHours(-id), text, AudioMilliseconds: 1000, DecodeMilliseconds: 100, TargetApp: "notepad");
    }
}
