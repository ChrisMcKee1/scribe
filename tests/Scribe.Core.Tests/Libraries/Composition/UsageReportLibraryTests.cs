using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The usage report's library labels (W1b contracts 3.3.6, acceptance C-9 and C-9b, review finding A6): shareable only
/// when AI cleanup may carry the library, bound to the content its permission covered, and handed over only through the
/// vocabulary source's gate.
/// </summary>
public sealed class UsageReportLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly HistoryEntry[] History =
    [
        Entry(1, "Kubernetes rollout with Helm and Nightjar"),
        Entry(2, "Kubernetes, Helm, Nightjar and Contoso again"),
        Entry(3, "Signature Block and Contoso"),
        Entry(4, "the sig goes under the sig line"),
    ];

    [Fact]
    public void Only_labels_of_libraries_AI_cleanup_may_carry_are_shareable_and_the_seam_ids_are_not_read()
    {
        var source = Source(teamPermitted: true, canaryPermitted: false);
        source.SeamEntries = [DictionaryEntry.New("contoso", "SeamCanary")];

        var report = UsageReport.Build(
            new StubHistory(History), new StubDictionary([DictionaryEntry.New("contoso", "Contoso")]), source,
            enabledLibraryIds: ["everything", "the", "projection", "says"], periodDays: null, Now);

        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Kubernetes", Covered: true, Shareable: true });   // permitted
        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Helm", Covered: true, Shareable: true });
        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Nightjar", Covered: true, Shareable: false });    // kept from AI cleanup
        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Contoso", Covered: true, Shareable: true });      // the dictionary, as before
        Assert.DoesNotContain(report.Snapshot.Terms, t => t.Text == "SeamCanary");
        Assert.Equal(["team"], report.LibraryScope.PermittedLibraryIds);
        Assert.Equal(H1, report.LibraryScope.PermittedContent["team"]);
    }

    [Fact]
    public void A_permitted_librarys_template_is_not_shareable_either()
    {
        // Release 0.4.4's rule and W1b's both hold: a replacement that spans lines is a template, whoever permits it.
        var team = CustomLibrary("team", Custom("sig", "Signature Block\n-- sent from Scribe"), Custom("kube", "Kubernetes"));
        var catalog = Catalog(State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]), Committed(team, H1));
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(catalog));

        var report = UsageReport.Build(new StubHistory(History), new StubDictionary([]), source, [], periodDays: null, Now);

        Assert.Contains(report.Snapshot.Terms, t =>
            t is { Covered: true, Shareable: false } && t.Text.StartsWith("Signature Block", StringComparison.Ordinal));
        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Kubernetes", Shareable: true });
    }

    [Fact]
    public void A_service_that_is_not_a_vocabulary_source_shares_no_library_label()
    {
        var plain = new PlainLibraries([DictionaryEntry.New("kube", "Kubernetes")]);

        var report = UsageReport.Build(
            new StubHistory(History), new StubDictionary([DictionaryEntry.New("contoso", "Contoso")]), plain, ["team"], periodDays: null, Now);

        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Kubernetes", Covered: true, Shareable: false });
        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Contoso", Covered: true, Shareable: true });
        Assert.Same(AiVocabularyScope.None, report.LibraryScope);
    }

    [Fact]
    public void A_revocation_after_the_report_stops_the_insight_and_the_canary_never_reaches_the_transport()
    {
        // C-9b: the report is built with the canary library permitted; then its permission is revoked and published.
        var source = Source(teamPermitted: false, canaryPermitted: true);
        var report = UsageReport.Build(new StubHistory(History), new StubDictionary([]), source, [], periodDays: null, Now);
        Assert.Contains(report.Snapshot.Terms, t => t is { Text: "Nightjar", Shareable: true });
        var transport = new List<string>();

        source.Publish(Source(teamPermitted: false, canaryPermitted: false).Current);

        var sent = source.TryHandOff(report.LibraryScope, () => transport.Add(UsageInsight.BuildSummary(report.Snapshot)));
        Assert.False(sent);
        Assert.Empty(transport);
    }

    [Fact]
    public void A_report_with_only_dictionary_labels_is_never_held_up()
    {
        var source = Source(teamPermitted: false, canaryPermitted: false);
        var report = UsageReport.Build(
            new StubHistory(History), new StubDictionary([DictionaryEntry.New("contoso", "Contoso")]), source, [], periodDays: null, Now);
        Assert.Same(AiVocabularyScope.None, report.LibraryScope);

        // Whatever is revoked afterwards, the hand-off goes.
        source.PublishScope(AiVocabularyScope.None);
        var transport = new List<string>();
        Assert.True(source.TryHandOff(report.LibraryScope, () => transport.Add("insight")));
        Assert.Equal(["insight"], transport);
    }

    [Fact]
    public void The_scope_binds_only_the_libraries_behind_shareable_labels()
    {
        // Both team and canary are permitted, but only team's term turns up in history: revoking canary later must not
        // hold up a report that never carried its labels.
        var team = CustomLibrary("team", Custom("kube", "Kubernetes"));
        var canary = CustomLibrary("canary", Custom("unused codename", "Unheard"));
        var catalog = Catalog(
            State(enabled: ["team", "canary"], ai: [("team", true), ("canary", true)], accepted: [("team", H1), ("canary", H2)]),
            Committed(team, H1), Committed(canary, H2));
        var source = new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(catalog));
        var report = UsageReport.Build(new StubHistory(History), new StubDictionary([]), source, [], periodDays: null, Now);
        Assert.Equal(["team"], report.LibraryScope.PermittedLibraryIds);

        var revoked = Catalog(
            State(enabled: ["team", "canary"], ai: [("team", true), ("canary", false)], accepted: [("team", H1), ("canary", H2)]),
            Committed(team, H1), Committed(canary, H2));
        source.Publish(LibraryComposer.Instance.ComposeVocabulary(revoked));

        Assert.True(source.TryHandOff(report.LibraryScope, () => { }));
    }

    private static FakeVocabularySource Source(bool teamPermitted, bool canaryPermitted)
    {
        var team = CustomLibrary("team", Custom("kube", "Kubernetes"), Custom("helm", "Helm"));
        var canary = CustomLibrary("canary", Custom("nightjar", "Nightjar"));
        var catalog = Catalog(
            State(enabled: ["team", "canary"], ai: [("team", teamPermitted), ("canary", canaryPermitted)], accepted: [("team", H1), ("canary", H2)]),
            Committed(team, H1), Committed(canary, H2));
        return new FakeVocabularySource(LibraryComposer.Instance.ComposeVocabulary(catalog));
    }

    private static HistoryEntry Entry(long id, string text) =>
        new(id, Now.AddHours(-id), text, AudioMilliseconds: 1000, DecodeMilliseconds: 100, TargetApp: "notepad");

    private sealed class StubHistory(IReadOnlyList<HistoryEntry> entries) : IHistoryRepository
    {
        public HistoryEntry Add(HistoryEntry entry) => throw new NotSupportedException();

        public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio) => throw new NotSupportedException();

        public long AddAudioBlob(CapturedAudio audio) => throw new NotSupportedException();

        public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100) => [.. entries.Take(limit)];

        public CapturedAudio? GetAudio(long blobId) => throw new NotSupportedException();

        public void SetAiRating(long id, AiRating rating) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public int PruneOlderThan(DateTimeOffset cutoffUtc) => throw new NotSupportedException();
    }

    private sealed class StubDictionary(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> GetAll() => entries;

        public IReadOnlyList<DictionaryEntry> GetEnabled() => [.. entries.Where(e => e.Enabled)];

        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public void Update(DictionaryEntry entry) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void SaveAll(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();

        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();
    }

    private sealed class PlainLibraries(IReadOnlyList<DictionaryEntry> entries) : PostProcessing.IDictionaryLibraryService
    {
        public IReadOnlyList<PostProcessing.DictionaryLibrary> GetLibraries() => [];

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => entries;

        public PostProcessing.DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }
}
