using System.Text;
using System.Text.Json;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The downgrade-safe projection and the reading of an older build's changes (W1b contracts 3.3.4, 6.2): the shared
/// legacy id of a hand-placed twin (C-7b, A15), round trips through the row that is the truth (C-7c, A17), and a list
/// that is safe before and after a commit's physical changes (C-7d, A18). The property tests run 0.4.3's own selection
/// (<see cref="Legacy043LibrarySelection"/>) over real folders holding the files an older build could meet.
/// </summary>
public sealed class LibraryStateEncodingTests : IDisposable
{
    private static readonly LibraryIdentity GitHub = BuiltInIdentity("github");
    private static readonly LibraryIdentity Twin = CustomIdentity("custom-github", "github.csv");
    private static readonly LibraryIdentity Team = CustomIdentity("team-terms");
    private static readonly LibraryStateContext FirstStart = new(false, false, false);
    private static readonly LibraryStateContext Later = new(false, false, GenerationStored: true, CommitWitnessed: true);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribe-lib-encoding-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }

    // --- C-7b ---

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]    // the twin kept from AI cleanup
    [InlineData(true, false, true, true, false)]    // the twin off
    [InlineData(false, true, true, true, false)]    // the built-in off
    [InlineData(true, true, true, false, false)]    // the built-in kept from AI cleanup
    public void The_shared_legacy_id_is_written_only_while_both_twins_are_on_and_permitted(
        bool gitHubOn, bool twinOn, bool twinAi, bool gitHubAi, bool written)
    {
        IReadOnlyList<LibraryIdentity> identities = [GitHub, Twin, Team];
        var enabled = new List<string> { "team-terms" };
        if (gitHubOn)
        {
            enabled.Add("github");
        }

        if (twinOn)
        {
            enabled.Add("custom-github");
        }

        var state = State(enabled: enabled, ai: [("github", gitHubAi), ("custom-github", twinAi), ("team-terms", true)],
            accepted: [("custom-github", H1), ("team-terms", H2)]);

        var encoded = LibraryComposer.Instance.EncodeLocalState(state, null, identities, identities);

        Assert.Equal(written, encoded.EnabledLibraryIds.Contains("github"));
        Assert.Contains("team-terms", encoded.EnabledLibraryIds);
        using var row = JsonDocument.Parse(encoded.StateValue!);
        Assert.Equal(
            enabled.Order(StringComparer.Ordinal),
            row.RootElement.GetProperty("enabled").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Across_random_groups_an_older_build_never_applies_a_library_the_state_has_off_or_kept_from_AI_cleanup()
    {
        // C-7b's property: an adoption moves no file, so the older build meets exactly these files.
        var random = new Random(20260929);
        for (var round = 0; round < 150; round++)
        {
            var scenario = Scenario.Random(random, withPhysicalChanges: false);
            var encoded = LibraryComposer.Instance.EncodeLocalState(scenario.State, null, scenario.Before, scenario.After);
            scenario.CheckEveryMix(encoded.EnabledLibraryIds, Path.Combine(_root, "b" + round), $"round {round}");
        }
    }

    // --- C-7c ---

    [Fact]
    public void Round_trips_keep_each_twins_own_choices_and_apply_an_older_builds_shared_flag_to_both()
    {
        IReadOnlyList<LibraryIdentity> identities = [GitHub, Twin, Team, BuiltInIdentity("scribe.future"), CustomIdentity("alpha")];
        var catalog = Catalog(
            0, LibraryLocalState.Absent,
            Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"))),
            Committed(BuiltInLibrary("scribe.future", Shipped("future", "Future"))),
            Committed(CustomLibrary("custom-github", Custom("nightjar", "Nightjar")), H1, fileName: "github.csv"),
            Committed(CustomLibrary("team-terms", Custom("kube", "K8s")), H2),
            Committed(CustomLibrary("alpha", Custom("helm", "Helm")), H3));

        // (1) The first adoption over 0.4.3's ["github"]: both twins on, the twin Existing and permitted.
        var read = LibraryComposer.Instance.ReadLocalState(["github", "scribe.future"], null, identities, FirstStart);
        var adopted = LibraryComposer.Instance.PlanAdoption(Catalog(0, read, [.. catalog.Libraries]), FirstStart)!.State;
        Assert.Equal(["custom-github", "github", "scribe.future"], adopted.EnabledIds.Order(StringComparer.Ordinal));
        Assert.True(adopted.AiPermissions["custom-github"]);
        var first = LibraryComposer.Instance.EncodeLocalState(adopted, null, identities, identities);
        Assert.Equal(["github", "scribe.future"], first.EnabledLibraryIds);
        AssertRow(first, enabled: ["custom-github", "github", "scribe.future"], projection: ["github", "scribe.future"]);

        // (2) A Save with both enabled and permitted encodes the same.
        var saved = LibraryComposer.Instance.EncodeLocalState(adopted, adopted, identities, identities);
        Assert.Equal(first.EnabledLibraryIds, saved.EnabledLibraryIds);

        // (3) Reading that pair back, as a restart does, keeps both twins, and the next encoding still writes github.
        var restarted = LibraryComposer.Instance.ReadLocalState(saved.EnabledLibraryIds, saved.StateValue, identities, Later);
        Assert.Equal(["custom-github", "github", "scribe.future"], restarted.EnabledIds.Order(StringComparer.Ordinal));
        Assert.Contains("github", LibraryComposer.Instance.EncodeLocalState(restarted, restarted, identities, identities).EnabledLibraryIds);

        // (4) An older build turns the shared flag off: both twins off. And on again: both on, AI permission unchanged.
        var off = LibraryComposer.Instance.ReadLocalState(["scribe.future"], saved.StateValue, identities, Later);
        Assert.DoesNotContain("github", off.EnabledIds);
        Assert.DoesNotContain("custom-github", off.EnabledIds);
        var twinDenied = State(enabled: ["github"], ai: [("custom-github", false)], accepted: [("custom-github", H1)]);
        var denied = LibraryComposer.Instance.EncodeLocalState(twinDenied, null, identities, identities);
        Assert.Empty(denied.EnabledLibraryIds);
        var on = LibraryComposer.Instance.ReadLocalState(["github"], denied.StateValue, identities, Later);
        Assert.Contains("github", on.EnabledIds);
        Assert.Contains("custom-github", on.EnabledIds);
        Assert.False(AiVocabularyPolicy.IsPermitted(on, "custom-github", false, H1));
        Assert.DoesNotContain("github", LibraryComposer.Instance.EncodeLocalState(on, on, identities, identities).EnabledLibraryIds);

        // (5) With no document list (a session on defaults) the row is used as stored.
        var onDefaults = LibraryComposer.Instance.ReadLocalState(null, saved.StateValue, identities, Later with { RunningOnDefaults = true });
        Assert.Equal(restarted.EnabledIds.Order(StringComparer.Ordinal), onDefaults.EnabledIds.Order(StringComparer.Ordinal));
        Assert.Equal(["github", "scribe.future"], onDefaults.LegacyEnabledIds.Order(StringComparer.Ordinal));

        // (6) An unrelated id an older build adds or removes changes only its own group, and a scribe. built-in id an
        // older build cannot list is not turned off by its absence.
        var added = LibraryComposer.Instance.ReadLocalState(["github", "alpha"], saved.StateValue, identities, Later);
        Assert.Equal(["alpha", "custom-github", "github", "scribe.future"], added.EnabledIds.Order(StringComparer.Ordinal));
        var withTeam = State(enabled: ["github", "custom-github", "team-terms"], ai: [("custom-github", true), ("team-terms", true)],
            accepted: [("custom-github", H1), ("team-terms", H2)]);
        var teamSaved = LibraryComposer.Instance.EncodeLocalState(withTeam, null, identities, identities);
        var teamRemoved = LibraryComposer.Instance.ReadLocalState(["github"], teamSaved.StateValue, identities, Later);
        Assert.Equal(["custom-github", "github"], teamRemoved.EnabledIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Across_random_stored_pairs_and_older_build_edits_reading_applies_exactly_the_older_builds_changes()
    {
        var random = new Random(20260930);
        for (var round = 0; round < 400; round++)
        {
            var scenario = Scenario.Random(random, withPhysicalChanges: false);
            var identities = scenario.After;
            var stored = LibraryComposer.Instance.EncodeLocalState(scenario.State, null, identities, identities);
            var legacyIds = identities.Select(i => i.LegacyId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var projection = stored.EnabledLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var edited = random.Next(4) == 0;
            var document = stored.EnabledLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (edited)
            {
                foreach (var legacyId in legacyIds)
                {
                    if (Unlistable(identities, legacyId))
                    {
                        // An older build's Save drops what its window cannot list.
                        document.Remove(legacyId);
                    }
                    else if (random.Next(3) == 0 && !document.Remove(legacyId))
                    {
                        document.Add(legacyId);
                    }
                }
            }

            var read = LibraryComposer.Instance.ReadLocalState([.. document], stored.StateValue, identities, Later);

            foreach (var library in identities)
            {
                var was = scenario.State.EnabledIds.Contains(library.Id);
                var now = read.EnabledIds.Contains(library.Id);
                var turnedOn = document.Contains(library.LegacyId) && !projection.Contains(library.LegacyId);
                var turnedOff = projection.Contains(library.LegacyId) && !document.Contains(library.LegacyId) &&
                    !Unlistable(identities, library.LegacyId);
                var context = $"round {round}, {library.Id}: was {was}, now {now}, on {turnedOn}, off {turnedOff}";
                Assert.True(was == now || edited, context);
                Assert.True(was || !now || turnedOn, context);
                Assert.True(!turnedOff || !now, context);
                Assert.True(!turnedOn || now, context);

                var accepted = AiVocabularyPolicy.AcceptedContentOf(scenario.State, library.Id);
                Assert.True(
                    !AiVocabularyPolicy.IsPermitted(read, library.Id, library.BuiltIn, accepted) ||
                    AiVocabularyPolicy.IsPermitted(scenario.State, library.Id, library.BuiltIn, accepted),
                    context);
            }
        }
    }

    // --- C-7d ---

    [Fact]
    public void A_group_is_written_only_when_it_is_safe_before_and_after_the_commits_physical_changes()
    {
        IReadOnlyList<LibraryIdentity> before = [GitHub, Twin, Team];
        IReadOnlyList<LibraryIdentity> afterDeletingTwin = [GitHub, Team];

        // The twin kept from AI cleanup is deleted while its move to Recently deleted may be deferred: github stays out.
        var starting = State(enabled: ["github", "custom-github", "team-terms"], ai: [("custom-github", false), ("team-terms", true)],
            accepted: [("custom-github", H1), ("team-terms", H2)]);
        var deleting = State(enabled: ["github", "team-terms"], ai: [("team-terms", true)], accepted: [("team-terms", H2)]);
        Assert.DoesNotContain("github", LibraryComposer.Instance.EncodeLocalState(deleting, starting, before, afterDeletingTwin).EnabledLibraryIds);
        Assert.Contains("github", LibraryComposer.Instance.EncodeLocalState(deleting, deleting, afterDeletingTwin, afterDeletingTwin).EnabledLibraryIds);

        // A group every member of which stays, enabled and permitted, is written; so is a created library.
        var created = CustomIdentity("custom-release-notes");
        var both = State(enabled: ["github", "custom-github", "team-terms", "custom-release-notes"],
            ai: [("custom-github", true), ("team-terms", true), ("custom-release-notes", true)],
            accepted: [("custom-github", H1), ("team-terms", H2), ("custom-release-notes", H3)]);
        Assert.Equal(
            ["github", "custom-release-notes", "team-terms"],
            LibraryComposer.Instance.EncodeLocalState(both, starting, before, [.. before, created]).EnabledLibraryIds);

        // A custom library rewritten under a permission granted in the same Save stays out until a later commit.
        var privateBefore = State(enabled: ["team-terms"], ai: [("team-terms", false)], accepted: [("team-terms", H1)]);
        var grantedAndRewritten = State(enabled: ["team-terms"], ai: [("team-terms", true)], accepted: [("team-terms", H2)]);
        Assert.Empty(LibraryComposer.Instance.EncodeLocalState(grantedAndRewritten, privateBefore, [Team], [Team]).EnabledLibraryIds);
        Assert.Equal(["team-terms"], LibraryComposer.Instance.EncodeLocalState(grantedAndRewritten, grantedAndRewritten, [Team], [Team]).EnabledLibraryIds);

        // One rewritten while already permitted is written: ordinary edits never drop it from an older build's list.
        var permittedBefore = State(enabled: ["team-terms"], ai: [("team-terms", true)], accepted: [("team-terms", H1)]);
        Assert.Equal(["team-terms"], LibraryComposer.Instance.EncodeLocalState(grantedAndRewritten, permittedBefore, [Team], [Team]).EnabledLibraryIds);

        // A built-in whose edits change is judged by the new state alone: older builds load only its shipped rows.
        var editsBefore = State(enabled: ["github"], ai: [("github", false)], accepted: [("github", H1)]);
        var editsAfter = State(enabled: ["github"], ai: [("github", true)], accepted: [("github", H2)]);
        Assert.Equal(["github"], LibraryComposer.Instance.EncodeLocalState(editsAfter, editsBefore, [GitHub], [GitHub]).EnabledLibraryIds);

        // An adoption (no starting state, one list) is the rule of C-7b.
        Assert.Contains("github", LibraryComposer.Instance.EncodeLocalState(both, null, before, before).EnabledLibraryIds);
    }

    [Fact]
    public void Across_every_mix_of_the_files_before_and_after_a_commit_an_older_build_applies_only_what_the_states_permit()
    {
        var random = new Random(20261001);
        for (var round = 0; round < 90; round++)
        {
            var scenario = Scenario.Random(random, withPhysicalChanges: true);
            var encoded = LibraryComposer.Instance.EncodeLocalState(scenario.State, scenario.Starting, scenario.Before, scenario.After);
            scenario.CheckEveryMix(encoded.EnabledLibraryIds, Path.Combine(_root, "d" + round), $"round {round}");
        }
    }

    // --- C-5b ---

    [Fact]
    public void Every_encoding_keeps_a_lost_states_denial()
    {
        IReadOnlyList<LibraryIdentity> identities = [GitHub, Team];
        var lost = State(enabled: ["github", "team-terms"], ai: [("team-terms", true)], accepted: [("team-terms", H1)], lost: true);
        var unreadable = State(enabled: ["github"], health: LocalStateHealth.Unreadable);

        foreach (var (state, starting) in new[] { (lost, (LibraryLocalState?)null), (lost, lost), (unreadable, null) })
        {
            var encoded = LibraryComposer.Instance.EncodeLocalState(state, starting, identities, identities);
            using var row = JsonDocument.Parse(encoded.StateValue!);
            Assert.True(row.RootElement.GetProperty("aiPermissionsLost").GetBoolean());
            var read = LibraryComposer.Instance.ReadLocalState(encoded.EnabledLibraryIds, encoded.StateValue, identities, Later);
            Assert.True(read.AiPermissionsLost);
            Assert.False(AiVocabularyPolicy.IsPermitted(read, "github", true, null));
        }
    }

    private static bool Unlistable(IReadOnlyList<LibraryIdentity> identities, string legacyId)
    {
        var group = identities.Where(i => string.Equals(i.LegacyId, legacyId, StringComparison.OrdinalIgnoreCase)).ToList();
        return group.Count > 0 && group.All(i => i.BuiltIn && i.Id.StartsWith("scribe.", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRow(LibraryStateEncoding encoded, string[] enabled, string[] projection)
    {
        using var row = JsonDocument.Parse(encoded.StateValue!);
        Assert.Equal(enabled, row.RootElement.GetProperty("enabled").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(projection, row.RootElement.GetProperty("legacyProjection").EnumerateArray().Select(e => e.GetString()!));
    }

    /// <summary>
    /// A random commit over real built-ins, hand-placed twins of them and ordinary custom files, each custom file present
    /// before the commit, after it, or both, and rewritten or not, with the starting and committed states.
    /// </summary>
    private sealed class Scenario
    {
        private static readonly string[] BuiltInIds = ["github", "ai-terminology", "microsoft-azure", "scribe.future"];
        private static readonly string[] CustomStems = ["github", "ai-terminology", "alpha", "Team Notes", "zeta"];

        private readonly List<Member> _members = [];

        public required IReadOnlyList<LibraryIdentity> Before { get; init; }

        public required IReadOnlyList<LibraryIdentity> After { get; init; }

        public required LibraryLocalState State { get; init; }

        public LibraryLocalState? Starting { get; init; }

        private IReadOnlyList<Member> Members => _members;

        public static Scenario Random(Random random, bool withPhysicalChanges)
        {
            var members = new List<Member>();
            foreach (var id in BuiltInIds.Where(_ => random.Next(3) != 0))
            {
                members.Add(new Member(new LibraryIdentity(id, true, null), InBefore: true, InAfter: true, Rewritten: random.Next(3) == 0)
                {
                    HashBefore = Hash('8'),
                    HashAfter = Hash('9'),
                });
            }

            var hashes = 0;
            foreach (var stem in CustomStems.Where(_ => random.Next(2) == 0))
            {
                var twin = BuiltInIds.Contains(stem);
                var identity = new LibraryIdentity(twin ? "custom-" + stem : stem, false, stem + ".csv");
                var presence = withPhysicalChanges ? random.Next(4) : 0;
                members.Add(new Member(identity, InBefore: presence != 2, InAfter: presence != 1,
                    Rewritten: withPhysicalChanges && presence is 0 or 3 && random.Next(2) == 0)
                {
                    HashBefore = Hash((char)('a' + hashes++ % 6)),
                    HashAfter = Hash((char)('a' + hashes++ % 6)),
                });
            }

            LibraryLocalState RandomState(bool after) => LibraryLocalState.Create(
                members.Where(m => (after ? m.InAfter : m.InBefore) && random.Next(4) != 0).Select(m => m.Identity.Id),
                null,
                members.Where(_ => random.Next(3) != 0).Select(m => new KeyValuePair<string, bool>(m.Identity.Id, random.Next(3) != 0)),
                null,
                null,
                LocalStateHealth.Ok,
                members
                    .Where(m => (after ? m.InAfter : m.InBefore) && random.Next(8) != 0 && (!m.Identity.BuiltIn || random.Next(2) == 0))
                    .Select(m => new KeyValuePair<string, LibraryContentHash>(m.Identity.Id, m.HashFor(after))),
                aiPermissionsLost: random.Next(10) == 0);

            var starting = RandomState(after: false);
            var state = RandomState(after: true);
            var scenario = new Scenario
            {
                Before = [.. members.Where(m => m.InBefore).Select(m => m.Identity)],
                After = [.. members.Where(m => m.InAfter).Select(m => m.Identity)],
                State = state,
                Starting = withPhysicalChanges ? starting : null,
            };
            scenario._members.AddRange(members);
            return scenario;
        }

        /// <summary>Every mix of files an older build could meet until the commit's files are all in place.</summary>
        public void CheckEveryMix(IReadOnlyList<string> list, string folder, string context)
        {
            var customs = Members.Where(m => !m.Identity.BuiltIn).ToList();
            var mixes = new List<string?[]> { new string?[customs.Count] };
            for (var i = 0; i < customs.Count; i++)
            {
                var versions = new List<string?> { null };
                if (customs[i].InBefore)
                {
                    versions.Add(customs[i].Rewritten ? "before" : "same");
                }

                if (customs[i].InAfter)
                {
                    versions.Add(customs[i].Rewritten ? "after" : "same");
                }

                mixes = [.. mixes.SelectMany(mix => versions.Distinct().Select(version =>
                {
                    var next = (string?[])mix.Clone();
                    next[i] = version;
                    return next;
                }))];
            }

            // Without physical changes the older build meets the files as they are, all of them.
            if (Starting is null)
            {
                mixes = [[.. customs.Select(_ => (string?)"same")]];
            }

            var number = 0;
            foreach (var mix in mixes)
            {
                var mixFolder = Path.Combine(folder, "mix" + number++);
                Directory.CreateDirectory(mixFolder);
                for (var i = 0; i < customs.Count; i++)
                {
                    if (mix[i] is { } version)
                    {
                        File.WriteAllText(Path.Combine(mixFolder, customs[i].Identity.FileName!),
                            $"pattern,replacement\nzq {customs[i].Identity.Id} one,{customs[i].Identity.Id}|{version}\n", Encoding.UTF8);
                    }
                }

                var applied = Legacy043LibrarySelection.EnabledEntries(list, mixFolder);
                foreach (var entry in applied.Where(e => e.Replacement.Contains('|', StringComparison.Ordinal)))
                {
                    var parts = entry.Replacement.Split('|');
                    var member = customs.Single(m => m.Identity.Id == parts[0]);
                    var permitted = parts[1] == "before"
                        ? AiVocabularyPolicy.IsPermitted(Starting!, member.Identity.Id, false, member.HashBefore)
                        : AiVocabularyPolicy.IsPermitted(State, member.Identity.Id, false, member.HashFor(after: true));
                    Assert.True(State.EnabledIds.Contains(member.Identity.Id) && permitted,
                        $"{context}: an older build applies {member.Identity.Id} ({parts[1]}) with the list [{string.Join(", ", list)}]");
                }

                foreach (var builtIn in Members.Where(m => m.Identity.BuiltIn && list.Contains(m.Identity.Id, StringComparer.OrdinalIgnoreCase)))
                {
                    Assert.True(
                        State.EnabledIds.Contains(builtIn.Identity.Id) &&
                        AiVocabularyPolicy.IsPermitted(State, builtIn.Identity.Id, true, AiVocabularyPolicy.AcceptedContentOf(State, builtIn.Identity.Id)),
                        $"{context}: an older build applies the built-in {builtIn.Identity.Id}");
                }
            }
        }

        private sealed record Member(LibraryIdentity Identity, bool InBefore, bool InAfter, bool Rewritten)
        {
            public LibraryContentHash HashBefore { get; init; }

            public LibraryContentHash HashAfter { get; init; }

            public LibraryContentHash HashFor(bool after) => after && Rewritten ? HashAfter : HashBefore;
        }
    }
}
