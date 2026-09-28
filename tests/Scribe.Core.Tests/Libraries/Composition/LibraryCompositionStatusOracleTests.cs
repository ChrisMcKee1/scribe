using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.CompositionOracle;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// LB3 (407ef46, adc2c17) keeps a spoken form's first row inline in the statuses' index and makes a status's set of listed
/// libraries only once a second library holds the form. 10c9a0b's composition, copied verbatim
/// (<see cref="LibraryCompositionAt10c9a0b"/>, whose IndexRowsByKey and StatusOf read its own state), decides every status
/// of seeded catalogs, the libraries each names in order, and everything else the composition answers.
/// </summary>
/// <remarks>
/// <para>
/// The catalogs are built to reach every shape the index handles: packs mostly off, forms many packs share, a pack holding
/// a form twice (a custom pack's rows, a built-in's renamed row), built-ins and custom packs sharing forms, rows turned off,
/// unusable files, content not yet accepted with the markers the upgrade gives it, reviews and personal entries. Half of
/// them build built-in rows through the overlay, as the editor gets them, and half build rows directly, shapes the overlay
/// never gives included, which a composition reads all the same. The test counts what it reached, so a generator that
/// stopped reaching a shape fails it. Seeds are fixed, each with a <see cref="Random"/> of its own, and a failure names
/// its seed.
/// </para>
/// <para>
/// Negative controls, made by hand and undone: in LibraryComposition.IndexRowsByKey, a second holder assigned to the local
/// copy (<c>holders = holders with { ... }</c>) instead of written back (<c>rows[key] = ...</c>) loses every holder after a
/// form's first, and a status lists no other library where 10c9a0b lists one; in StatusOf, the set made at a form's second
/// holder seeded with this library alone (without that holder's) lists a pack that holds the form twice twice. The test
/// fails on each, at one of its first seeds.
/// </para>
/// </remarks>
public sealed class LibraryCompositionStatusOracleTests(ITestOutputHelper output)
{
    // Two built-ins BuiltInOrder lists, so precedence ranks them first, and three it does not, ranked by id.
    private static readonly string[] BuiltIns = ["ai-terminology", "github", "bi-one", "bi-two", "bi-three"];

    [Fact]
    public void Every_status_and_answer_is_what_10c9a0b_s_composition_gives_for_seeded_catalogs()
    {
        var reach = new Reach();
        ForEachSeed(407_000, 1_000, (seed, random) =>
        {
            var catalog = RandomCatalog(random, BuiltIns, mostlyOff: seed % 4 != 3, anyFileState: true, canonicalBuiltIns: seed % 2 == 0);
            var dictionary = RandomDictionary(random);
            var budget = new GlossaryBudget(Pick(random, Budgets));
            var contents = catalog.Libraries.Select(library => library.Content).ToList();
            var (ids, keys) = (LibraryIds(contents), Keys(contents));

            var oracle = LibraryCompositionAt10c9a0b.Committed(catalog, dictionary, budget);
            AssertSameLines(
                Read(CompositionView.Of(oracle), ids, keys, dictionary, budget),
                Read(CompositionView.Of(LibraryComposition.Committed(catalog, dictionary, budget)), ids, keys, dictionary, budget),
                $"committed, seed {seed}");
            reach.Note(catalog, catalog.LocalState, contents, keys, oracle.StatusOf);
            if (seed % 4 != 3)
            {
                reach.NoteMostlyOff(catalog);
            }

            // A draft of the same catalog, previewed: the composition takes its sources from the draft and the files.
            var draft = RandomDraft(random, catalog);
            var draftContents = draft.Libraries.Select(library => library.Content).ToList();
            var (draftIds, draftKeys) = (LibraryIds(draftContents), Keys(draftContents));
            var oraclePreview = LibraryCompositionAt10c9a0b.Preview(draft, catalog, dictionary, budget);
            AssertSameLines(
                Read(CompositionView.Of(oraclePreview), draftIds, draftKeys, dictionary, budget),
                Read(
                    CompositionView.Of(LibraryComposition.Preview(draft, catalog, dictionary, budget, LibraryDecisions.Precedence)),
                    draftIds, draftKeys, dictionary, budget),
                $"preview, seed {seed}");
            reach.Note(catalog, draft.LocalState, draftContents, draftKeys, oraclePreview.StatusOf);
        });

        output.WriteLine(reach.ToString());
        reach.AssertEveryShape();
    }

    // What the seeded statuses reached, from the oracle's answers and the catalogs.
    private sealed class Reach
    {
        private readonly HashSet<TermWinner> _winners = [];
        private readonly HashSet<GlossaryInclusion> _glossary = [];
        private readonly HashSet<TermMarker> _markers = [];
        private int _statuses;
        private int _twoOrMoreListed;
        private int _bothLists;
        private int _heldTwice;
        private int _builtInAndCustom;
        private int _libraries;
        private int _librariesOn;

        public void Note(
            LibraryCatalog catalog,
            LibraryLocalState state,
            IReadOnlyList<LibraryContent> contents,
            IReadOnlyList<LibraryTermKey> keys,
            Func<string, LibraryTermKey, TermStatus> statusOf)
        {
            var builtIn = contents.ToDictionary(content => content.Id, content => content.BuiltIn, StringComparer.OrdinalIgnoreCase);
            foreach (var content in contents)
            {
                var on = state.EnabledIds.Contains(content.Id) &&
                    LibraryTiers.IsUsable(catalog.Find(content.Id)?.State ?? LibraryFileState.Available);
                foreach (var key in keys)
                {
                    var status = statusOf(content.Id, key);
                    _statuses++;
                    _winners.Add(status.Winner);
                    _glossary.Add(status.Glossary);
                    _markers.Add(status.Marker);
                    var listed = status.SameResultIn.Concat(status.DifferentResultIn).ToList();
                    _twoOrMoreListed += listed.Count >= 2 ? 1 : 0;
                    _bothLists += status.SameResultIn.Count > 0 && status.DifferentResultIn.Count > 0 ? 1 : 0;
                    _builtInAndCustom += listed.Any(id => builtIn.TryGetValue(id, out var other) && other != content.BuiltIn) ? 1 : 0;
                    var rowsHolding = content.Rows.Count(row => row.Values.Enabled && LibraryTiers.CompetingKey(row) == key);
                    _heldTwice += on && rowsHolding >= 2 && listed.Count > 0 ? 1 : 0;
                }
            }
        }

        public void NoteMostlyOff(LibraryCatalog catalog)
        {
            _libraries += catalog.Libraries.Count;
            _librariesOn += catalog.Libraries.Count(library => catalog.LocalState.EnabledIds.Contains(library.Content.Id));
        }

        public void AssertEveryShape()
        {
            Assert.True(_twoOrMoreListed >= 2_000, $"Only {_twoOrMoreListed} statuses listed two or more libraries.");
            Assert.True(_bothLists >= 200, $"Only {_bothLists} statuses listed libraries on both sides.");
            Assert.True(_heldTwice >= 300, $"Only {_heldTwice} statuses of a pack in use holding its form twice listed another.");
            Assert.True(_builtInAndCustom >= 4_500, $"Only {_builtInAndCustom} statuses listed a pack of the other kind.");
            Assert.True(_librariesOn * 5 < _libraries * 2, $"{_librariesOn} of {_libraries} packs were on in the mostly-off catalogs.");
            Assert.Equal(Enum.GetValues<TermWinner>().Order(), _winners.Order());
            Assert.Equal(Enum.GetValues<GlossaryInclusion>().Order(), _glossary.Order());
            Assert.Equal(Enum.GetValues<TermMarker>().Order(), _markers.Order());
        }

        public override string ToString() =>
            $"{_statuses} statuses: {_twoOrMoreListed} listing two or more, {_bothLists} on both sides, {_heldTwice} of a pack " +
            $"holding its form twice, {_builtInAndCustom} across built-ins and custom packs; {_librariesOn} of {_libraries} " +
            $"packs on in the mostly-off catalogs; winners [{string.Join(",", _winners.Order())}], glossary [{string.Join(",", _glossary.Order())}], " +
            $"markers [{string.Join(",", _markers.Order())}].";
    }
}
