using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
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
        [
            new("Kube ", "Kube", WholeWord: true, Enabled: true), new(" north star", "North star", WholeWord: true, Enabled: false),
            new(string.Empty, "Nothing", WholeWord: true, Enabled: true), new(null, null, WholeWord: true, Enabled: true),
        ];

        var plan = LibrarySwitchOffCopy.Plan(rows, [team], [Usage(team, unused: 9)]);

        Assert.Equal(["retro=Retro"], Describe(plan.Copies));
        Assert.Equal(1, plan.Collided);
    }

    [Fact]
    public void A_row_off_in_the_dictionary_is_not_reported_when_what_stays_on_writes_the_same()
    {
        var alpha = Library("alpha", builtIn: false, ("llm", "LLM"));
        var beta = Library("beta", builtIn: false, ("llm", "LLM"));

        var plan = LibrarySwitchOffCopy.Plan([new("llm", "LLM", WholeWord: true, Enabled: false)], [alpha, beta], [Usage(alpha, unused: 2)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.Collided);
    }

    [Fact]
    public void A_copy_keeps_the_written_form_and_word_boundary_and_is_on()
    {
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [new DictionaryEntry(0, "dot net", ".NET", WholeWord: false, Enabled: true)]);

        var copy = Assert.Single(LibrarySwitchOffCopy.Plan([], [team], [Usage(team, unused: 1)]).Copies);

        Assert.Equal(new DictionaryEntry(0, "dot net", ".NET", WholeWord: false, Enabled: true), copy);
    }

    [Fact]
    public void A_row_whose_spoken_form_never_matches_dictated_text_is_not_copied_into_one_that_would()
    {
        // Dictated text is trimmed and its spaces collapsed before any rule runs, so a spoken form padded with spaces never
        // matches: this row changes nothing today. Save trims a copy's spoken form, so a copy would start rewriting
        // "dot net". The loader and Save both trim, so such a row can only be built by hand.
        var team = new DictionaryLibrary("team-terms", "Team terms", "Custom", Description: null, BuiltIn: false,
            [new DictionaryEntry(0, "  dot net  ", ".NET", WholeWord: false, Enabled: true)]);

        var plan = LibrarySwitchOffCopy.Plan([], [team], [Usage(team, unused: 1)]);

        Assert.Empty(plan.Copies);
        Assert.Equal(Dictated([], [team], ["use dot net", "dot net"]), Dictated(plan.Copies, [], ["use dot net", "dot net"]));
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

    // The next five cases compare finished text before and after the switch through the real repository, composition
    // and post-processor. A spoken form's key (trimmed, OrdinalIgnoreCase) decides which row the composer keeps, but the
    // matcher is an invariant case-insensitive regular expression, which folds letters differently: it does not treat
    // the Greek final sigma as sigma, which OrdinalIgnoreCase does, and it treats the Kelvin sign (U+212A) as k, which
    // OrdinalIgnoreCase does not. Rule order also breaks ties between rules that match the same text.

    [Fact]
    public void A_rule_behind_the_winner_whose_spoken_form_only_compares_equal_is_not_the_same_rule()
    {
        // Astra's first case. The composer keeps alpha's "ΟΣ" and drops beta's "ος" as the same spoken form, but alpha's rule
        // rewrites "ΟΣ" and "οσ" and leaves "ος", and beta's would do the opposite. The review switches alpha off for its
        // unused row; without a copy of "ΟΣ", every one of these inputs changes.
        var alpha = Library("alpha", builtIn: false, ("ΟΣ", "Expansion"), ("unused term", "Unused Term"));
        var beta = Library("beta", builtIn: false, ("ος", "Expansion"));
        var usage = UsageFromHistory([alpha, beta], "Expansion was the word we needed", "alpha");

        var plan = LibrarySwitchOffCopy.Plan([], [alpha, beta], [usage]);

        string[] inputs = ["ΟΣ", "ος", "οσ", "το ΟΣ μας", "το ος μας"];
        var before = Dictated([], [alpha, beta], inputs);
        Assert.Equal(["Expansion", "ος", "Expansion", "το Expansion μας", "το ος μας"], before);
        Assert.Equal(before, Dictated(plan.Copies, [beta], inputs));
        Assert.Equal(["ΟΣ=Expansion"], Describe(plan.Copies));
    }

    [Fact]
    public void An_identical_rule_behind_the_winner_is_not_the_same_when_another_rule_now_beats_it_to_the_text()
    {
        // Astra's second case. Gamma writes exactly what alpha writes, but beta's Kelvin-sign rule, a spoken form of its own
        // to the composer, matches "k" too. Alpha's rule comes before beta's, so "k" is First; with alpha off and nothing
        // copied, beta's comes before gamma's and "k" becomes Second.
        var alpha = Library("alpha", builtIn: false, ("k", "First"), ("unused term", "Unused Term"));
        var beta = Library("beta", builtIn: false, (Kelvin, "Second"));
        var gamma = Library("gamma", builtIn: false, ("k", "First"));
        var usage = UsageFromHistory([alpha, beta, gamma], "First things first", "alpha");

        var plan = LibrarySwitchOffCopy.Plan([], [alpha, beta, gamma], [usage]);

        string[] inputs = ["k", "K", Kelvin, "plan k now"];
        var before = Dictated([], [alpha, beta, gamma], inputs);
        Assert.Equal(["First", "First", "First", "plan First now"], before);
        Assert.Equal(before, Dictated(plan.Copies, [beta, gamma], inputs));
        Assert.Equal(["k=First"], Describe(plan.Copies));
    }

    [Fact]
    public void A_kept_term_that_an_earlier_library_rule_already_beats_is_not_copied_ahead_of_it()
    {
        // The mirror image: the Kelvin-sign rule comes first, so "k" is Second today although zebra's row is the composer's
        // rule for "k". A copy would go ahead of every library rule and make it First, so leaving it out is what keeps it.
        var aardvark = Library("aardvark", builtIn: false, (Kelvin, "Second"));
        var zebra = Library("zebra", builtIn: false, ("k", "First"), ("unused term", "Unused Term"));
        var usage = UsageFromHistory([aardvark, zebra], "First things first", "zebra");

        var plan = LibrarySwitchOffCopy.Plan([], [aardvark, zebra], [usage]);

        string[] inputs = ["k", "K", Kelvin, "plan k now"];
        var before = Dictated([], [aardvark, zebra], inputs);
        Assert.Equal(["Second", "Second", "Second", "plan Second now"], before);
        Assert.Equal(before, Dictated(plan.Copies, [aardvark], inputs));
        Assert.Empty(plan.Copies);
    }

    [Fact]
    public void A_kept_term_that_a_dictionary_row_already_beats_is_not_copied_ahead_of_that_row()
    {
        // The dictionary is read sorted by spoken form, byte by byte in UTF-8, so a copy of "k" (0x6B) would sort before
        // the Kelvin-sign row (0xE2 0x84 0xAA) that writes "k" today.
        var dictionary = new[] { DictionaryEntry.New(Kelvin, "Dictionary") };
        var zebra = Library("zebra", builtIn: false, ("k", "First"), ("unused term", "Unused Term"));
        var usage = UsageFromHistory([zebra], "First things first", "zebra");

        var plan = LibrarySwitchOffCopy.Plan(Rows(dictionary), [zebra], [usage]);

        string[] inputs = ["k", "K", Kelvin];
        var before = Dictated(dictionary, [zebra], inputs);
        Assert.Equal(["Dictionary", "Dictionary", "Dictionary"], before);
        Assert.Equal(before, Dictated([.. dictionary, .. plan.Copies], [], inputs));
        Assert.Empty(plan.Copies);
    }

    [Fact]
    public void Kept_terms_whose_copies_would_swap_places_are_copied_only_as_far_as_that_keeps_the_text()
    {
        // Both rows are rules dictation compiles and both are kept. In the library the Kelvin-sign row comes first and wins
        // "k"; copied together, the dictionary's byte order would put "k" first and flip the result. Copying the
        // Kelvin-sign row alone keeps every input as it is.
        var team = Library("team", builtIn: false, (Kelvin, "Second"), ("k", "First"), ("unused term", "Unused Term"));
        var usage = UsageFromHistory([team], "First and Second", "team");

        var plan = LibrarySwitchOffCopy.Plan([], [team], [usage]);

        string[] inputs = ["k", "K", Kelvin];
        var before = Dictated([], [team], inputs);
        Assert.Equal(["Second", "Second", "Second"], before);
        Assert.Equal(before, Dictated(plan.Copies, [], inputs));
        Assert.Equal([$"{Kelvin}=Second"], Describe(plan.Copies));
    }

    [Fact]
    public void Three_kept_spellings_the_matcher_reads_as_one_word_copy_only_the_one_that_wins_today()
    {
        // Three spoken forms that are three keys to the composer (the Kelvin sign, k, and ß beside capital ẞ) but one word
        // to the matcher. The first row wins today. The dictionary's byte order is the reverse of the rows' order, so
        // copying all three puts the last one first; copying only the first keeps every input as it is.
        const string first = Kelvin + "\u1E9E", second = Kelvin + "ß", third = "k\u1E9E";
        var team = Library("team", builtIn: false, (first, "One"), (second, "Two"), (third, "Three"), ("unused term", "Unused Term"));
        var usage = UsageFromHistory([team], "One Two Three", "team");

        var plan = LibrarySwitchOffCopy.Plan([], [team], [usage]);

        string[] inputs = [first, second, third, "kß", "Kß"];
        var before = Dictated([], [team], inputs);
        Assert.Equal(["One", "One", "One", "One", "One"], before);
        Assert.Equal(before, Dictated(plan.Copies, [], inputs));
        Assert.Equal([$"{first}=One"], Describe(plan.Copies));
    }

    [Fact]
    public void Across_random_libraries_the_switch_changes_what_dictation_writes_only_where_the_cleanup_drops_a_rule_or_says_so()
    {
        // Spoken forms where the composer's key and the matcher disagree, beside plain ones: the Greek final sigma (one key
        // with its capital and medial forms, but another word to the matcher), the Kelvin sign and the capital sharp s (keys
        // of their own, but the same word as k and ß to the matcher), dotted and dotless i (different words to both), and a
        // multi-word form with a shorter one inside it. Each family writes one of two forms, so rows often agree exactly.
        (string Spoken, string[] Written)[] forms =
        [
            ("acme", ["Acme", "ACME"]),
            ("get hub", ["GitHub", "Get Hub"]), ("hub", ["Hub", "HUB"]),
            ("ΛΟΓΟΣ", ["Λόγος", "ΛΟΓΟΣ"]), ("λογος", ["Λόγος", "ΛΟΓΟΣ"]), ("λογοσ", ["Λόγος", "ΛΟΓΟΣ"]),
            ("k", ["First", "Second"]), ("K", ["First", "Second"]), (Kelvin, ["First", "Second"]),
            ("strasse", ["Straße", "STRASSE"]), ("straße", ["Straße", "STRASSE"]), ("STRA\u1E9EE", ["Straße", "STRASSE"]),
            ("istanbul", ["Istanbul", "\u0130stanbul"]), ("\u0130stanbul", ["Istanbul", "\u0130stanbul"]),
            ("\u0131stanbul", ["Istanbul", "\u0130stanbul"]),
        ];
        (string Id, bool BuiltIn)[] ids =
        [
            ("github", true), ("ai-terminology", true), ("microsoft-azure", true), ("github", false),
            ("alpha", false), ("beta", false), ("team-terms", false), ("team-terms-2", false),
        ];

        // The plan's promise is decided on each spoken form and its invariant lower and upper case, so the property checks
        // exactly those, for every form in the round, whether or not the plan looked at it.
        string[] probes = [.. forms.SelectMany(f => CaseVariants(f.Spoken)).Distinct(StringComparer.Ordinal)];

        var random = new Random(24092026);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string outcome, int by = 1) => counts[outcome] = counts.GetValueOrDefault(outcome) + by;

        for (var round = 0; round < 800; round++)
        {
            var libraries = ids.Where(_ => random.Next(10) < 6).Select(id => RandomLibrary(id.Id, id.BuiltIn)).ToList();
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

            // The dictionary as Save would take it: no spoken form twice.
            var dictionary = new List<DictionaryEntry>();
            foreach (var form in forms.Where(_ => random.Next(10) == 0))
            {
                var entry = RandomEntry(form) with { Enabled = random.Next(10) < 6 };
                if (!Saves([.. dictionary, entry]).HasDuplicate)
                {
                    dictionary.Add(entry);
                }
            }

            var plan = LibrarySwitchOffCopy.Plan(
                Rows(dictionary).OrderBy(_ => random.Next()), libraries.OrderBy(_ => random.Next()), switching.OrderBy(_ => random.Next()));

            var off = libraries.Where(l => switching.Any(u => IsFor(u, l))).ToList();
            var staying = libraries.Where(l => !off.Contains(l)).ToList();
            bool Kept(DictionaryLibrary library, DictionaryEntry row) =>
                switching.Any(u => IsFor(u, library) && u.KeepTerms.Contains(row));
            bool Blocked(DictionaryEntry row) =>
                Saves([.. dictionary, DictionaryEntry.New(row.Pattern, row.Replacement, row.WholeWord)]).HasDuplicate;

            // The rows dictation compiles today, through the production composer, with the dictionary in the order the
            // repository reads it back.
            var compiled = DictionaryLibraryComposer.Merge(
                    Saves(dictionary).Entries.Where(e => e.Enabled).OrderBy(e => e.Pattern, DictionaryRepository.PatternOrder),
                    DictionaryLibraryComposer.ComposeLibraries(libraries))
                .ToHashSet<DictionaryEntry>(ReferenceEqualityComparer.Instance);

            // What the cleanup promises: every kept row of a switched-off library that dictation applies today stays exactly
            // where it is, and every other row of it goes, including a kept row that never applies (another row beats it)
            // and a kept row the dictionary cannot take because a row there already has its spoken form (Save's own
            // duplicate rule decides that), which the plan has to report instead.
            var promised = libraries
                .Select(l => off.Contains(l)
                    ? l with { Entries = [.. l.Entries.Where(e => Kept(l, e) && compiled.Contains(e) && !Blocked(e))] }
                    : l)
                .ToList();

            var before = Dictation(dictionary, libraries);
            var intended = Dictation(dictionary, promised);
            var after = Dictation([.. dictionary, .. plan.Copies], staying);
            foreach (var probe in probes)
            {
                var was = before(probe);
                if (intended(probe) != was)
                {
                    // Dropping an unused rule, or losing a kept one the dictionary cannot take, changes this on purpose.
                    Count("exempt: the promise itself changes the text");
                    continue;
                }

                var now = after(probe);
                Assert.True(now == was, $"round {round}: '{probe}' was '{was}' and became '{now}'.\n" +
                    Scenario(dictionary, libraries, switching, plan));
                Count("checked: unchanged");
            }

            int blocked = 0, blockedAndChanged = 0;
            foreach (var library in off)
            {
                foreach (var row in library.Entries.Where(e => Kept(library, e)).Distinct<DictionaryEntry>(ReferenceEqualityComparer.Instance))
                {
                    if (!compiled.Contains(row))
                    {
                        // The round 2 finding: a switched-off library keeps a row another row beats, so nothing is copied.
                        Count("kept but never applied");
                        continue;
                    }

                    var variants = CaseVariants(row.Pattern.Trim());
                    if (Blocked(row))
                    {
                        blocked++;
                        blockedAndChanged += variants.Any(v => after(v) != before(v)) ? 1 : 0;
                        continue;
                    }

                    var copy = new DictionaryEntry(0, row.Pattern.Trim(), row.Replacement, row.WholeWord, Enabled: true);
                    if (plan.Copies.Contains(copy))
                    {
                        var withoutIt = Dictation([.. dictionary, .. plan.Copies.Where(c => c != copy)], staying);
                        Count(variants.Any(v => withoutIt(v) != before(v))
                            ? "copied: leaving it out changes the text"
                            : "copied: leaving it out keeps these forms, but no identical rule stays on");
                        if (RoundTwoCalledTheSame(row, staying))
                        {
                            Count("copied: round 2 called the rule behind it the same");
                        }
                    }
                    else
                    {
                        var withIt = Dictation([.. dictionary, .. plan.Copies, copy], staying);
                        Count(variants.Any(v => withIt(v) != before(v))
                            ? "left out: a copy would change the text"
                            : "left out: an identical rule stays on");
                    }
                }
            }

            Assert.InRange(plan.Collided, blockedAndChanged, blocked);
            Count("reported", plan.Collided);

            // Every copy is a kept, unblocked row dictation compiles today, and Save takes them all beside the dictionary.
            Assert.All(plan.Copies, c => Assert.Contains(
                off.SelectMany(l => l.Entries.Where(e => compiled.Contains(e) && Kept(l, e) && !Blocked(e))),
                e => new DictionaryEntry(0, e.Pattern.Trim(), e.Replacement, e.WholeWord, Enabled: true) == c));
            Assert.False(Saves([.. dictionary, .. plan.Copies]).HasDuplicate);
        }

        // The generator has to reach every outcome, or the property proves less than it claims.
        foreach (var (outcome, count) in counts)
        {
            output.WriteLine($"{count,7}  {outcome}");
        }

        string[] required =
        [
            "checked: unchanged", "exempt: the promise itself changes the text", "kept but never applied",
            "copied: leaving it out changes the text", "copied: round 2 called the rule behind it the same",
            "copied: leaving it out keeps these forms, but no identical rule stays on",
            "left out: an identical rule stays on", "left out: a copy would change the text", "reported",
        ];
        Assert.All(required, outcome => Assert.True(
            counts.GetValueOrDefault(outcome) >= 20, $"'{outcome}' was reached {counts.GetValueOrDefault(outcome)} times"));

        DictionaryLibrary RandomLibrary(string id, bool builtIn)
        {
            var entries = new List<DictionaryEntry>();
            foreach (var form in forms.Where(_ => random.Next(10) < 3))
            {
                entries.Add(RandomEntry(form) with { Enabled = random.Next(10) < 9 });
                if (random.Next(20) == 0)
                {
                    // A second row for the same spoken form: dead code inside the library.
                    entries.Add(RandomEntry(form));
                }
            }

            return new DictionaryLibrary(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn, entries);
        }

        DictionaryEntry RandomEntry((string Spoken, string[] Written) form) =>
            DictionaryEntry.New(form.Spoken, form.Written[random.Next(form.Written.Length)], wholeWord: random.Next(10) < 8);
    }

    private static bool IsFor(LibraryUsage usage, DictionaryLibrary library) =>
        usage.Id == library.Id && usage.BuiltIn == library.BuiltIn;

    // A failing round, in full, so it can be turned into a test of its own.
    private static string Scenario(
        IEnumerable<DictionaryEntry> dictionary,
        IEnumerable<DictionaryLibrary> libraries,
        IEnumerable<LibraryUsage> switching,
        LibrarySwitchOffCopy.Result plan)
    {
        static string Row(DictionaryEntry e) =>
            $"{Escape(e.Pattern)}={Escape(e.Replacement)}{(e.WholeWord ? string.Empty : " (substring)")}{(e.Enabled ? string.Empty : " (off)")}";
        static string Escape(string s) => string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:X4}"));

        var text = new System.Text.StringBuilder();
        text.AppendLine("dictionary: " + string.Join(", ", dictionary.Select(Row)));
        foreach (var library in LibraryPrecedence.Order(libraries))
        {
            var usage = switching.FirstOrDefault(u => IsFor(u, library));
            text.AppendLine($"{library.Id} ({(library.BuiltIn ? "built-in" : "custom")}){(usage is null ? string.Empty : ", switched off")}: " +
                string.Join(", ", library.Entries.Select(e => Row(e) + (usage is not null && usage.KeepTerms.Contains(e) ? " (kept)" : string.Empty))));
        }

        text.AppendLine($"copies: {string.Join(", ", plan.Copies.Select(Row))}; collided {plan.Collided}");
        return text.ToString();
    }

    // The spoken form as it is and in invariant lower and upper case.
    private static IEnumerable<string> CaseVariants(string spoken) =>
        new[] { spoken, spoken.ToLowerInvariant(), spoken.ToUpperInvariant() }.Distinct(StringComparer.Ordinal);

    // Classifies an outcome, never decides one: whether the comparison round 2 used (spoken forms OrdinalIgnoreCase, the
    // written form and word-boundary rule as they are) called the rule a library that stays on supplies the same.
    private static bool RoundTwoCalledTheSame(DictionaryEntry row, IEnumerable<DictionaryLibrary> staying) =>
        DictionaryLibraryComposer.ComposeLibraries(staying)
            .FirstOrDefault(e => string.Equals(e.Pattern.Trim(), row.Pattern.Trim(), StringComparison.OrdinalIgnoreCase)) is { } next &&
        string.Equals(next.Replacement, row.Replacement, StringComparison.Ordinal) && next.WholeWord == row.WholeWord;

    // What Save stores for these entries, and whether it would refuse them for a repeated spoken form.
    private static DictionaryEntryBuilder.Result Saves(IEnumerable<DictionaryEntry> entries) =>
        DictionaryEntryBuilder.Build([.. entries.Select(e => new DictionaryEntryBuilder.Row(0, e.Pattern, e.Replacement, e.WholeWord, e.Enabled))]);

    /// <summary>
    /// Finished text from the real post-processor over this dictionary and these libraries, with the dictionary stored as
    /// Save stores it and read back in the repository's order.
    /// </summary>
    private static Func<string, string> Dictation(IEnumerable<DictionaryEntry> dictionary, IEnumerable<DictionaryLibrary> libraries)
    {
        var processor = new TextPostProcessor(
            new StoredDictionary(Saves(dictionary).Entries), NullLogger<TextPostProcessor>.Instance, snippets: null,
            libraries: new ComposedLibraries([.. libraries]));
        return processor.Process;
    }

    private static string? WrittenAfter(LibrarySwitchOffCopy.Result plan, IEnumerable<DictionaryLibrary> staying, string spoken) =>
        Dictation(plan.Copies, staying)(spoken);

    /// <summary>
    /// A dictionary read back in the order <see cref="DictionaryRepository"/> returns it, which
    /// <c>DictionaryRepositoryOrderTests</c> pins against SQLite, without a database per round.
    /// </summary>
    private sealed class StoredDictionary(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        private readonly IReadOnlyList<DictionaryEntry> _all = [.. entries.OrderBy(e => e.Pattern, DictionaryRepository.PatternOrder)];

        public IReadOnlyList<DictionaryEntry> GetAll() => _all;

        public IReadOnlyList<DictionaryEntry> GetEnabled() => [.. _all.Where(e => e.Enabled)];

        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public void Update(DictionaryEntry entry) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void SaveAll(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();

        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();
    }

    private static DictionaryLibrary Library(string id, bool builtIn, params (string Spoken, string Written)[] terms) =>
        new(id, id, builtIn ? "Built-in" : "Custom", Description: null, builtIn,
            [.. terms.Select(t => DictionaryEntry.New(t.Spoken, t.Written))]);

    // The Kelvin sign, U+212A: its own spoken form to the composer, the letter k to the matcher.
    private const string Kelvin = "\u212A";

    /// <summary>
    /// What dictation writes for each input: the dictionary stored as Save stores it (trimmed) in a real repository, which
    /// reads it back sorted by spoken form, the libraries through the real composition, and the real post-processor.
    /// </summary>
    private static string[] Dictated(IEnumerable<DictionaryEntry> dictionary, IEnumerable<DictionaryLibrary> libraries, IEnumerable<string> inputs)
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new DictionaryRepository(database);
        var saved = DictionaryEntryBuilder.Build(
            [.. dictionary.Select(e => new DictionaryEntryBuilder.Row(0, e.Pattern, e.Replacement, e.WholeWord, e.Enabled))]).Entries;
        if (saved.Count > 0)
        {
            repository.AddRange(saved);
        }

        var processor = new TextPostProcessor(
            repository, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: new ComposedLibraries([.. libraries]));
        return [.. inputs.Select(processor.Process)];
    }

    // The review's verdict on one library, from the real analyzer over a single dictation.
    private static LibraryUsage UsageFromHistory(IReadOnlyList<DictionaryLibrary> enabled, string transcript, string id) =>
        DictionaryUsageAnalyzer.Analyze([transcript], [], enabled, minimumTranscripts: 1, minimumWords: 1)
            .Libraries.Single(library => library.Id == id);

    private static IReadOnlyList<LibrarySwitchOffCopy.Row> Rows(IEnumerable<DictionaryEntry> dictionary) =>
        [.. dictionary.Select(e => new LibrarySwitchOffCopy.Row(e.Pattern, e.Replacement, e.WholeWord, e.Enabled))];

    /// <summary>The enabled libraries as the real service hands them to dictation: through the composer.</summary>
    private sealed class ComposedLibraries(IReadOnlyList<DictionaryLibrary> enabled) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => LibraryPrecedence.Order(enabled);

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => DictionaryLibraryComposer.ComposeLibraries(enabled);

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }

    // The review keeps every term of these small libraries, as it keeps any term it finds a trace of.
    private static LibraryUsage Usage(DictionaryLibrary library, int unused) =>
        new(library.Id, library.Name, [.. library.EnabledEntries], unused, library.BuiltIn);

    private static IReadOnlyList<string> Describe(IEnumerable<DictionaryEntry> copies) =>
        copies.Select(e => $"{e.Pattern}={e.Replacement}").ToList();
}
