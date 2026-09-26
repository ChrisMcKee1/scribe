using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;
using ScriptedDictionary = Scribe.Core.Tests.Vocabulary.VocabularyPublisherTests.ScriptedDictionary;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The signatures the Settings window compares its dictionary, snippet and library rows by (round 5, G3). A Save skips a
/// section whose rows sign like the saved ones, and the Save's draft reads only whether they do, so a signature that joins
/// user text with a delimiter lets a changed row sign like the saved row: the Save skips the edited section, and a Save that
/// waits for its vocabulary closes over the edit. Release 0.4.4 and rounds 1 to 4 joined the fields with an unescaped '|'
/// and the rows with a U+001F, which a pasted value can hold. The window's signatures are these, pinned by
/// <see cref="SaveDraftCoverageTests"/>.
/// </summary>
public sealed class SectionSignatureTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void Groks_snippet_pair_signs_differently()
    {
        SnippetBuilder.Row[] saved = [new(1, "a", "b|c", true)];
        SnippetBuilder.Row[] edited = [new(1, "a|b", "c", true)];

        // The join both signed as, and the stored rows it hid: a different phrase and template.
        Assert.Equal("1|a|b|c|True", Joined(saved));
        Assert.Equal(Joined(saved), Joined(edited));
        Assert.NotEqual(SnippetBuilder.Build(saved).Snippets.Single(), SnippetBuilder.Build(edited).Snippets.Single());

        Assert.NotEqual(Sign(saved), Sign(edited));
    }

    [Fact]
    public void A_pattern_and_replacement_that_trade_a_delimiter_sign_differently()
    {
        DictionaryEntryBuilder.Row[] saved = [new(1, "a", "b|c", true, true)];
        DictionaryEntryBuilder.Row[] edited = [new(1, "a|b", "c", true, true)];

        Assert.Equal("1|a|b|c|True|True", Joined(saved));
        Assert.Equal(Joined(saved), Joined(edited));
        Assert.NotEqual(DictionaryEntryBuilder.Build(saved).Entries.Single(), DictionaryEntryBuilder.Build(edited).Entries.Single());

        Assert.NotEqual(Sign(saved), Sign(edited));
    }

    [Fact]
    public void Rows_whose_text_swallows_a_row_boundary_sign_differently()
    {
        // Two rows, and one row whose template or replacement holds what the join wrote at the boundary (a pasted U+001F
        // among it): the same text.
        SnippetBuilder.Row[] twoSnippets = [new(1, "a", "b", true), new(2, "c", "d", true)];
        SnippetBuilder.Row[] oneSnippet = [new(1, "a", "b|True\u001f2|c|d", true)];
        Assert.Equal(Joined(twoSnippets), Joined(oneSnippet));
        Assert.NotEqual(Sign(twoSnippets), Sign(oneSnippet));

        DictionaryEntryBuilder.Row[] twoEntries = [new(1, "a", "b", true, true), new(2, "c", "d", true, true)];
        DictionaryEntryBuilder.Row[] oneEntry = [new(1, "a", "b|True|True\u001f2|c|d", true, true)];
        Assert.Equal(Joined(twoEntries), Joined(oneEntry));
        Assert.NotEqual(Sign(twoEntries), Sign(oneEntry));

        // The libraries' rows are framed the same way, although today's ids (slugs, or a file name, which cannot hold '|')
        // could not collide.
        Assert.NotEqual(Sign([("a|True", true)]), Sign([("a", true), ("", true)]));
        Assert.NotEqual(Sign([("team", true)]), Sign([("team", false)]));
        Assert.NotEqual(Sign([("a", true), ("b", false)]), Sign([("b", false), ("a", true)]));
    }

    [Fact]
    public void Distinct_rows_never_sign_alike_and_null_differs_from_empty()
    {
        var texts = new string?[] { null, string.Empty, "a", "b", "|", "a|b", "True", "|True", "1|a", "\u001f", "a\u001f0|b", "s1:a" };
        var snippets =
            from id in new long[] { 0, 1 }
            from phrase in texts
            from template in texts
            from enabled in new[] { true, false }
            select new SnippetBuilder.Row(id, phrase, template, enabled);
        AssertDistinct(snippets.Select(row => (Key: row.ToString(), Hash: Sign([row]))));

        var entries =
            from pattern in texts
            from replacement in texts
            from wholeWord in new[] { true, false }
            select new DictionaryEntryBuilder.Row(0, pattern, replacement, wholeWord, true);
        AssertDistinct(entries.Select(row => (Key: row.ToString(), Hash: Sign([row]))));

        Assert.NotEqual(Sign([new SnippetBuilder.Row(0, null, string.Empty, true)]), Sign([new SnippetBuilder.Row(0, string.Empty, string.Empty, true)]));
        Assert.NotEqual(Sign(Array.Empty<SnippetBuilder.Row>()), Sign([new SnippetBuilder.Row(0, null, null, true)]));
    }

    [Fact]
    public void A_colliding_edit_is_an_unsaved_change_so_a_save_no_longer_skips_its_section()
    {
        // The Save's own check: it writes a section only when its rows differ from the snapshot of what storage holds. With
        // the joined signature the edited snippet was "untouched", and the next Save skipped it (release 0.4.4's defect).
        SnippetBuilder.Row[] saved = [new(1, "a", "b|c", true)];
        SnippetBuilder.Row[] edited = [new(1, "a|b", "c", true)];

        var joined = Loaded(Joined(saved));
        Assert.False(joined.HasChanges(Joined(edited)));

        var framed = Loaded(Sign(saved));
        Assert.False(framed.HasChanges(Sign(saved)));
        Assert.True(framed.HasChanges(Sign(edited)));

        // The dictionary, and the row-boundary shift, the same way.
        DictionaryEntryBuilder.Row[] savedEntries = [new(1, "a", "b", true, true), new(2, "c", "d", true, true)];
        DictionaryEntryBuilder.Row[] shifted = [new(1, "a", "b|True|True\u001f2|c|d", true, true)];
        Assert.False(Loaded(Joined(savedEntries)).HasChanges(Joined(shifted)));
        Assert.True(Loaded(Sign(savedEntries)).HasChanges(Sign(shifted)));
        Assert.True(Loaded(Sign([new DictionaryEntryBuilder.Row(1, "a", "b|c", true, true)])).HasChanges(Sign([new DictionaryEntryBuilder.Row(1, "a|b", "c", true, true)])));
    }

    [Theory]
    [InlineData("snippet pair")]
    [InlineData("dictionary pair")]
    [InlineData("row boundary")]
    public async Task A_colliding_edit_during_a_held_save_keeps_it_open(string collision)
    {
        // Grok's G3 sequence, with the Save's build held: the Save marked the rows saved and watches its draft, whose rows
        // part is whether the dictionary and the snippets differ from what storage holds; the still-editable window turns
        // the saved rows into rows that joined the same; the build runs.
        var dictionary = new ScriptedDictionary([]);
        var queued = new List<Action>();
        using var publisher = new VocabularyPublisher(
            new TestVocabularySource(Of(3)),
            dictionary,
            new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance),
            NullLogger<VocabularyPublisher>.Instance,
            queued.Add);
        var starting = publisher.StartAsync();
        Assert.Single(queued)();
        queued.Clear();
        await starting.WaitAsync(Bound);

        var edited = false;
        var (framedNow, joinedNow) = collision switch
        {
            "snippet pair" => Rows<SnippetBuilder.Row>([new(1, "a", "b|c", true)], [new(1, "a|b", "c", true)], () => edited, Sign, Joined),
            "dictionary pair" => Rows<DictionaryEntryBuilder.Row>([new(1, "a", "b|c", true, true)], [new(1, "a|b", "c", true, true)], () => edited, Sign, Joined),
            _ => Rows<DictionaryEntryBuilder.Row>(
                [new(1, "a", "b", true, true), new(2, "c", "d", true, true)],
                [new(1, "a", "b|True|True\u001f2|c|d", true, true)],
                () => edited,
                Sign,
                Joined),
        };
        var framed = Loaded(framedNow());
        var joined = Loaded(joinedNow());
        var completing = StoredChangeAcknowledgement.Watch(publisher.RefreshAsync(), () => Draft(framed.HasChanges(framedNow()))).CompleteAsync();
        var closingOver = StoredChangeAcknowledgement.Watch(publisher.RefreshAsync(), () => Draft(joined.HasChanges(joinedNow()))).CompleteAsync();

        var framedSaved = framedNow();
        var joinedSaved = joinedNow();
        edited = true;

        // The edit joins exactly as the saved rows did, and frames differently.
        Assert.Equal(joinedSaved, joinedNow());
        Assert.NotEqual(framedSaved, framedNow());
        Assert.Single(queued)();

        Assert.Equal(StoredChangeOutcome.ChangedWhileSaving, await completing.WaitAsync(Bound));

        // Round 4's joined signature saw no change and would have closed over the edit.
        Assert.Equal(StoredChangeOutcome.InEffect, await closingOver.WaitAsync(Bound));
    }

    // The framed and the joined signature of whichever rows are current: the saved ones, or the edited ones once edited.
    private static (Func<string> Framed, Func<string> Joined) Rows<TRow>(
        TRow[] saved, TRow[] edited, Func<bool> isEdited, Func<IReadOnlyList<TRow>, string> sign, Func<IReadOnlyList<TRow>, string> join) =>
        (() => sign(isEdited() ? edited : saved), () => join(isEdited() ? edited : saved));
    private static string Sign(IReadOnlyList<SnippetBuilder.Row> rows) => new DraftSnapshot().SnippetRows(rows).Hash();

    private static string Sign(IReadOnlyList<DictionaryEntryBuilder.Row> rows) => new DraftSnapshot().DictionaryRows(rows).Hash();

    private static string Sign(IReadOnlyList<(string Id, bool Enabled)> rows) => new DraftSnapshot().LibraryRows(rows).Hash();

    // The signatures release 0.4.4 and rounds 1 to 4 compared the rows by.
    private static string Joined(IReadOnlyList<SnippetBuilder.Row> rows) =>
        string.Join("\u001f", rows.Select(row => $"{row.Id}|{row.Phrase}|{row.Template}|{row.Enabled}"));

    private static string Joined(IReadOnlyList<DictionaryEntryBuilder.Row> rows) =>
        string.Join("\u001f", rows.Select(row => $"{row.Id}|{row.Pattern}|{row.Replacement}|{row.WholeWord}|{row.Enabled}"));

    // The window's draft reduced to its rows part, as it writes it (the flag of whichever section the case edits).
    private static string Draft(bool changed) => new DraftSnapshot().Part("rows").Flag(changed).Hash();

    // A section whose rows loaded and whose saved snapshot is the given signature, as the Save's MarkSaved leaves it.
    private static SettingsSectionLoad Loaded(string signature)
    {
        var load = new SettingsSectionLoad();
        Assert.True(load.TryBegin(null, out var ticket));
        Assert.True(load.Publish(ticket, signature));
        return load;
    }

    private static void AssertDistinct(IEnumerable<(string Key, string Hash)> signatures)
    {
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, hash) in signatures)
        {
            if (byHash.TryGetValue(hash, out var other))
            {
                Assert.Fail($"Two different rows signed alike: {other} and {key}");
            }

            byHash.Add(hash, key);
        }

        Assert.True(byHash.Count > 1);
    }
}
