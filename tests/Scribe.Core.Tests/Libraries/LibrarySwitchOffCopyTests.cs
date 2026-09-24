using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Xunit.Abstractions;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The dictionary cleanup copies a library's still-used terms into the dictionary before switching the library off.
/// A copy must keep what dictation writes: it is made only for the rule dictation applies today, across every library
/// that is on, and only when the libraries that stay on would not write the same thing anyway.
/// </summary>
public sealed class LibrarySwitchOffCopyTests(ITestOutputHelper output)
{
    [Fact]
    public void Switching_off_only_the_library_that_loses_a_spoken_form_copies_nothing_and_keeps_the_winner()
    {
        // "alpha.csv" precedes "beta.csv", so dictation writes Acme today. Only beta is switched off; copying its ACME into
        // the dictionary, which outranks every library, would override alpha although alpha stays on.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var beta = Library("beta", builtIn: false, ("acme", "ACME"));

        var plan = LibrarySwitchOffCopy.Plan([], [beta, alpha], [Usage(beta, unused: 7)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
        Assert.Equal("Acme", WrittenAfter(plan, [alpha], "acme"));
    }

    [Fact]
    public void Switching_off_the_winner_with_nothing_behind_it_copies_its_term()
    {
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var beta = Library("beta", builtIn: false, ("kube", "Kubernetes"));

        var plan = LibrarySwitchOffCopy.Plan([], [beta, alpha], [Usage(alpha, unused: 3)]);

        Assert.Equal(["acme=Acme"], Describe(plan.Copies));
        Assert.Equal("Acme", WrittenAfter(plan, [beta], "acme"));
    }

    [Fact]
    public void Switching_off_the_winner_copies_nothing_when_a_library_that_stays_on_writes_the_same()
    {
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var beta = Library("beta", builtIn: false, ("acme", "Acme"));

        var plan = LibrarySwitchOffCopy.Plan([], [alpha, beta], [Usage(alpha, unused: 3)]);

        Assert.Empty(plan.Copies);
        Assert.Equal("Acme", WrittenAfter(plan, [beta], "acme"));
    }

    [Fact]
    public void Switching_off_the_winner_copies_it_when_the_library_that_stays_on_writes_it_differently()
    {
        // Same written form but a different word-boundary rule is a different result, so it is not "the same anyway".
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"));
        var beta = new DictionaryLibrary("beta", "beta", "Custom", Description: null, BuiltIn: false,
            [DictionaryEntry.New("acme", "Acme", wholeWord: false)]);

        var plan = LibrarySwitchOffCopy.Plan([], [alpha, beta], [Usage(alpha, unused: 3)]);

        Assert.Equal(["acme=Acme"], Describe(plan.Copies));
        Assert.True(Assert.Single(plan.Copies).WholeWord);
    }

    [Fact]
    public void Two_libraries_switched_off_together_copy_only_the_precedence_winner()
    {
        // The review lists the library with the most unused terms first; the built-in precedes every custom library.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"), ("get hub", "GitHub Enterprise"));
        var beta = Library("beta", builtIn: false, ("acme", "ACME"));
        var github = Library("github", builtIn: true, ("get hub", "GitHub"));

        var plan = LibrarySwitchOffCopy.Plan(
            [], [beta, github, alpha], [Usage(beta, unused: 40), Usage(alpha, unused: 12), Usage(github, unused: 3)]);

        Assert.Equal(["get hub=GitHub", "acme=Acme"], Describe(plan.Copies));
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void A_winner_the_scan_found_no_trace_of_is_dropped_and_no_later_library_is_copied_in_its_place()
    {
        // alpha's acme is today's rule but is not kept (unused); beta's is kept, but it is not what dictation applies.
        var alpha = Library("alpha", builtIn: false, ("acme", "Acme"), ("kube", "Kubernetes"));
        var beta = Library("beta", builtIn: false, ("acme", "ACME"));
        var alphaKeepsKubeOnly = new LibraryUsage(alpha.Id, alpha.Name, [alpha.Entries[1]], UnusedCount: 1, BuiltIn: false);

        var plan = LibrarySwitchOffCopy.Plan([], [alpha, beta], [alphaKeepsKubeOnly, Usage(beta, unused: 1)]);

        Assert.Equal(["kube=Kubernetes"], Describe(plan.Copies));
    }

    [Fact]
    public void A_dictionary_row_that_is_on_keeps_the_spoken_form_and_one_that_is_off_is_reported()
    {
        var team = Library("team-terms", builtIn: false, ("kube", "Kubernetes"), ("north star", "North Star"), ("retro", "Retro"));
        LibrarySwitchOffCopy.Row[] rows =
            [new("Kube ", Enabled: true), new(" north star", Enabled: false), new(string.Empty, Enabled: true), new(null, Enabled: true)];

        var plan = LibrarySwitchOffCopy.Plan(rows, [team], [Usage(team, unused: 9)]);

        Assert.Equal(["retro=Retro"], Describe(plan.Copies));
        Assert.Equal(1, plan.Collided);
    }

    [Fact]
    public void A_row_off_in_the_dictionary_is_not_reported_when_what_stays_on_writes_the_same()
    {
        var alpha = Library("alpha", builtIn: false, ("llm", "LLM"));
        var beta = Library("beta", builtIn: false, ("llm", "LLM"));

        var plan = LibrarySwitchOffCopy.Plan([new("llm", Enabled: false)], [alpha, beta], [Usage(alpha, unused: 2)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void A_copy_keeps_the_written_form_and_word_boundary_trims_the_spoken_form_and_is_on()
    {
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [new DictionaryEntry(0, "  dot net  ", " .NET", WholeWord: false, Enabled: true)]);

        var copy = Assert.Single(LibrarySwitchOffCopy.Plan([], [team], [Usage(team, unused: 1)]).Copies);

        Assert.Equal(new DictionaryEntry(0, "dot net", " .NET", WholeWord: false, Enabled: true), copy);
    }

    [Fact]
    public void Only_the_terms_the_review_kept_are_copied()
    {
        var team = Library("team-terms", builtIn: false, ("kube", "Kubernetes"), ("retro", "Retro"));
        var usage = new LibraryUsage(team.Id, team.Name, [team.Entries[1]], UnusedCount: 1, BuiltIn: false);

        Assert.Equal(["retro=Retro"], Describe(LibrarySwitchOffCopy.Plan([], [team], [usage]).Copies));
    }

    [Fact]
    public void A_built_in_and_a_hand_placed_file_that_share_an_id_are_told_apart()
    {
        // Only the hand-placed file is switched off. The built-in stays on and comes first, so it keeps "get hub".
        var builtIn = Library("github", builtIn: true, ("get hub", "GitHub"));
        var file = Library("github", builtIn: false, ("get hub", "GitHub"));

        var plan = LibrarySwitchOffCopy.Plan([], [file, builtIn], [Usage(file, unused: 4)]);

        Assert.Empty(plan.Copies);
        Assert.Equal("GitHub", WrittenAfter(plan, [builtIn], "get hub"));

        // Both switched off, the built-in's row unused: the built-in's rule is today's and is dropped. The file's identical
        // row is kept but never applied, so it is not copied in its place.
        var builtInKeepsNothing = new LibraryUsage(builtIn.Id, builtIn.Name, [], UnusedCount: 1, BuiltIn: true);
        Assert.Empty(LibrarySwitchOffCopy.Plan([], [file, builtIn], [Usage(file, unused: 4), builtInKeepsNothing]).Copies);
    }

    [Fact]
    public void Only_the_row_dictation_applies_is_copied_when_a_library_repeats_a_spoken_form()
    {
        // A second row for the same spoken form never applies (the first wins), so keeping it keeps nothing dictation uses.
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [DictionaryEntry.New("acme", "Acme"), DictionaryEntry.New("acme", "ACME")]);
        var keepsTheSecondRow = new LibraryUsage(team.Id, team.Name, [team.Entries[1]], UnusedCount: 1, BuiltIn: false);

        Assert.Empty(LibrarySwitchOffCopy.Plan([], [team], [keepsTheSecondRow]).Copies);
        Assert.Equal(["acme=Acme"], Describe(LibrarySwitchOffCopy.Plan([], [team], [Usage(team, unused: 1)]).Copies));
    }

    [Fact]
    public void A_listed_library_that_is_not_on_copies_nothing()
    {
        // Its rules do not apply today, so there is nothing of it for the dictionary to keep.
        var on = Library("zeta", builtIn: false, ("kube", "K8s"));
        var notOn = Library("alpha", builtIn: false, ("kube", "Kubernetes"), ("retro", "Retro"));

        var plan = LibrarySwitchOffCopy.Plan([], [on], [Usage(notOn, unused: 1)]);

        Assert.Empty(plan.Copies);
    }

    [Fact]
    public void Across_random_libraries_dictation_writes_the_same_after_the_switch_except_for_dropped_or_reported_rules()
    {
        // Built-in and custom libraries, including a hand-placed file that reuses a built-in id, with shared spoken forms,
        // rows turned off inside libraries, dictionary rows on and off, and any subset switched off in any order.
        (string Id, bool BuiltIn)[] candidates =
        [
            ("github", true), ("ai-terminology", true), ("microsoft-azure", true), ("github", false),
            ("alpha", false), ("beta", false), ("team-terms", false), ("team-terms-2", false),
        ];
        string[] spoken = ["acme", "kube", "get hub", "north star", "sprint", "retro"];
        string[] written = ["Acme", "ACME", "acme corp"];
        var random = new Random(24092026);
        int unchanged = 0, losersKept = 0, dropped = 0, reported = 0, copiedToKeep = 0, keptByWhatStaysOn = 0;

        for (var round = 0; round < 3000; round++)
        {
            var libraries = candidates.Where(_ => random.Next(10) < 6).Select(c => RandomLibrary(c.Id, c.BuiltIn)).ToList();
            var switching = libraries
                .Where(_ => random.Next(10) < 4)
                .Select(l => new LibraryUsage(
                    l.Id, l.Name, [.. l.EnabledEntries.Where(_ => random.Next(10) < 6)], random.Next(1, 50), l.BuiltIn))
                .ToList();
            if (random.Next(10) == 0)
            {
                var notOn = RandomLibrary("not-on", builtIn: false);
                switching.Add(new LibraryUsage(notOn.Id, notOn.Name, [.. notOn.EnabledEntries], 1, BuiltIn: false));
            }

            var personal = spoken
                .Where(_ => random.Next(4) == 0)
                .Select(s => DictionaryEntry.New(s, written[random.Next(written.Length)]) with { Enabled = random.Next(10) < 6 })
                .ToList();

            var plan = LibrarySwitchOffCopy.Plan(
                personal.Select(p => new LibrarySwitchOffCopy.Row(p.Pattern, p.Enabled)).OrderBy(_ => random.Next()),
                libraries.OrderBy(_ => random.Next()),
                switching.OrderBy(_ => random.Next()));

            var ordered = LibraryPrecedence.Order(libraries);
            var staying = ordered.Where(l => !switching.Any(u => u.Id == l.Id && u.BuiltIn == l.BuiltIn)).ToList();
            var personalOn = personal.Where(p => p.Enabled).ToList();
            var before = Effective(personalOn, libraries);
            var after = Effective(personalOn.Concat(plan.Copies), staying);

            var expectedCollisions = 0;
            foreach (var key in spoken)
            {
                var context = $"round {round}, '{key}'";
                before.TryGetValue(key, out var was);
                after.TryGetValue(key, out var now);
                var copied = plan.Copies.Any(c => string.Equals(c.Pattern, key, StringComparison.OrdinalIgnoreCase));
                var winner = personalOn.Any(p => SameKey(p, key)) ? null : Winner(ordered, key);
                var usage = winner is null ? null : switching.FirstOrDefault(u => u.Id == winner.Value.Library.Id && u.BuiltIn == winner.Value.Library.BuiltIn);

                if (usage is null)
                {
                    // Written by the dictionary or by a library that stays on: unchanged, and nothing copied for it.
                    Assert.True(Same(was, now), context);
                    Assert.False(copied, context);
                    unchanged++;
                    if (winner is not null && switching.Any(u => u.KeepTerms.Any(t => SameKey(t, key))))
                    {
                        // The case the review found: a library switched off keeps a spoken form a library that stays on wins.
                        losersKept++;
                    }

                    continue;
                }

                if (!usage.KeepTerms.Contains(winner!.Value.Row))
                {
                    // Today's rule is dropped as unused, and no other library's version takes its place.
                    Assert.False(copied, context);
                    dropped++;
                    continue;
                }

                var fallback = Winner(staying, key);
                var sameAnyway = fallback is { } f && WritesTheSame(f.Row, winner.Value.Row);
                if (personal.Any(p => !p.Enabled && SameKey(p, key)) && !sameAnyway)
                {
                    // A dictionary row that is off blocks the copy, so the change is reported.
                    expectedCollisions++;
                    Assert.False(copied, context);
                    reported++;
                    continue;
                }

                Assert.True(Same(was, now), context);
                Assert.Equal(!sameAnyway, copied);
                if (sameAnyway)
                {
                    keptByWhatStaysOn++;
                }
                else
                {
                    copiedToKeep++;
                }
            }

            Assert.Equal(expectedCollisions, plan.Collided);
            Assert.Equal(plan.Copies.Count, plan.Copies.Select(c => c.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // The generator has to reach every outcome, or the property proves less than it claims.
        output.WriteLine($"unchanged {unchanged} (a switched-off library kept the loser in {losersKept}), dropped {dropped}, " +
                         $"reported {reported}, copied {copiedToKeep}, kept by what stays on {keptByWhatStaysOn}");
        Assert.All(
            new[] { unchanged, losersKept, dropped, reported, copiedToKeep, keptByWhatStaysOn },
            count => Assert.True(count >= 25, $"an outcome was reached only {count} times"));

        DictionaryLibrary RandomLibrary(string id, bool builtIn)
        {
            var entries = new List<DictionaryEntry>();
            foreach (var s in spoken.Where(_ => random.Next(2) == 0))
            {
                entries.Add(DictionaryEntry.New(s, written[random.Next(written.Length)], wholeWord: random.Next(10) < 7)
                    with { Enabled = random.Next(10) < 8 });
                if (random.Next(20) == 0)
                {
                    // A second row for the same spoken form: dead code inside the library.
                    entries.Add(DictionaryEntry.New(s, written[random.Next(written.Length)]));
                }
            }

            return new DictionaryLibrary(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn, entries);
        }
    }

    // An oracle independent of the code under test: the first enabled row for the spoken form, in precedence order.
    private static (DictionaryLibrary Library, DictionaryEntry Row)? Winner(IReadOnlyList<DictionaryLibrary> ordered, string key)
    {
        foreach (var library in ordered)
        {
            foreach (var entry in library.Entries)
            {
                if (entry.Enabled && SameKey(entry, key))
                {
                    return (library, entry);
                }
            }
        }

        return null;
    }

    // What dictation applies for each spoken form, through the real composition.
    private static Dictionary<string, DictionaryEntry> Effective(IEnumerable<DictionaryEntry> personalOn, IEnumerable<DictionaryLibrary> libraries) =>
        DictionaryLibraryComposer.Merge(personalOn, DictionaryLibraryComposer.ComposeLibraries(libraries))
            .ToDictionary(e => e.Pattern.Trim(), e => e, StringComparer.OrdinalIgnoreCase);

    private static string? WrittenAfter(LibrarySwitchOffCopy.Result plan, IEnumerable<DictionaryLibrary> staying, string spoken) =>
        Effective(plan.Copies, staying).TryGetValue(spoken, out var rule) ? rule.Replacement : null;

    private static bool SameKey(DictionaryEntry entry, string key) =>
        string.Equals(entry.Pattern.Trim(), key, StringComparison.OrdinalIgnoreCase);

    private static bool Same(DictionaryEntry? a, DictionaryEntry? b) =>
        a is null ? b is null : b is not null && WritesTheSame(a, b);

    private static bool WritesTheSame(DictionaryEntry a, DictionaryEntry b) =>
        string.Equals(a.Pattern.Trim(), b.Pattern.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Replacement, b.Replacement, StringComparison.Ordinal) &&
        a.WholeWord == b.WholeWord;

    private static DictionaryLibrary Library(string id, bool builtIn, params (string Spoken, string Written)[] terms) =>
        new(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn,
            [.. terms.Select(t => DictionaryEntry.New(t.Spoken, t.Written))]);

    // The review keeps every term of these small libraries, as it keeps any term it finds a trace of.
    private static LibraryUsage Usage(DictionaryLibrary library, int unused) =>
        new(library.Id, library.Name, [.. library.EnabledEntries], unused, library.BuiltIn);

    private static IReadOnlyList<string> Describe(IEnumerable<DictionaryEntry> copies) =>
        copies.Select(e => $"{e.Pattern}={e.Replacement}").ToList();
}
