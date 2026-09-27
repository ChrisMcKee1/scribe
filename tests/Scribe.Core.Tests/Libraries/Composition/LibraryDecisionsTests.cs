using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The two maintainer decisions sit behind one policy point each (W1b contracts 0 rule 6): Decision 2's table over every
/// origin, and each veto alternative being a change to <see cref="LibraryDecisions"/> alone (C-4).
/// </summary>
public sealed class LibraryDecisionsTests
{
    [Fact]
    public void Decision_2_turns_built_ins_and_libraries_existing_at_the_upgrade_on_and_everything_new_off()
    {
        bool?[] sources = [null, true, false];
        foreach (var origin in Enum.GetValues<LibraryOrigin>())
        {
            foreach (var source in sources)
            {
                Assert.True(LibraryDecisions.DefaultAiPermission(origin, builtIn: true, source), $"built-in {origin} {source}");

                var expected = origin switch
                {
                    LibraryOrigin.Existing => true,
                    LibraryOrigin.Duplicated or LibraryOrigin.RetiredBuiltIn => source ?? false,
                    LibraryOrigin.Created or LibraryOrigin.Imported or LibraryOrigin.Restored or LibraryOrigin.ChangedOutside
                        or LibraryOrigin.Discovered => false,
                    _ => throw new InvalidOperationException(
                        $"LibraryOrigin.{origin} is new: decide its Decision 2 default in LibraryDecisions and add it here."),
                };
                Assert.Equal(expected, LibraryDecisions.DefaultAiPermission(origin, builtIn: false, source));
            }
        }
    }

    [Fact]
    public void Decision_1_is_authored_terms_first_with_legacy_markers()
    {
        Assert.Equal(LibraryPrecedenceRule.AuthoredFirstWithLegacyMarkers, LibraryDecisions.Precedence);
    }

    [Fact]
    public void Each_decision_1_alternative_is_one_value_of_the_policy_point()
    {
        // "get hub" is a legacy row that contradicted the built-in at the upgrade; "kube" an authored row made since.
        var github = Lib.BuiltInLibrary("github", Lib.Shipped("get hub", "GitHub"), Lib.Shipped("kube", "Kubernetes"));
        var team = Lib.CustomLibrary("team", Lib.Custom("get hub", "GitHub Enterprise"), Lib.Custom("kube", "K8s"));
        var catalog = Lib.Catalog(
            Lib.State(enabled: ["github", "team"], markers: [("team", "get hub")], accepted: [("team", Lib.H1)]),
            Lib.Committed(github),
            Lib.Committed(team, Lib.H1));

        (string GetHub, string Kube) Winners(LibraryComposition composition) =>
            (Lib.Winner(composition, "get hub")!, Lib.Winner(composition, "kube")!);

        var budget = new GlossaryBudget(80);
        Assert.Equal(("GitHub", "K8s"), Winners(LibraryComposition.Committed(catalog, [], budget, LibraryPrecedenceRule.AuthoredFirstWithLegacyMarkers)));
        Assert.Equal(("GitHub Enterprise", "K8s"), Winners(LibraryComposition.Committed(catalog, [], budget, LibraryPrecedenceRule.AuthoredFirstEverywhere)));
        Assert.Equal(("GitHub", "Kubernetes"), Winners(LibraryComposition.Committed(catalog, [], budget, LibraryPrecedenceRule.ShippedFirst)));

        // The public factories read the policy point and nothing else.
        Assert.Equal(
            Winners(LibraryComposition.Committed(catalog, [], budget, LibraryDecisions.Precedence)),
            Winners(LibraryComposition.Committed(catalog, [], budget)));
    }

    [Fact]
    public void Each_decision_2_alternative_is_a_change_to_the_policy_point_alone()
    {
        // The adoption takes every default from the one delegate, so each veto moves what it records and nothing else.
        var catalog = Lib.Catalog(
            0,
            LibraryLocalState.Absent,
            Lib.Committed(Lib.BuiltInLibrary("github", Lib.Shipped("get hub", "GitHub"))),
            Lib.Committed(Lib.CustomLibrary("team", Lib.Custom("kube", "K8s")), Lib.H1));
        var firstStart = new LibraryStateContext(false, false, false);
        Func<LibraryOrigin, bool, bool?, bool> onEverywhere = (_, _, _) => true;
        Func<LibraryOrigin, bool, bool?, bool> offForEveryCustom = (_, builtIn, _) => builtIn;

        Assert.True(LibraryAdoptionPlanner.Plan(catalog, firstStart, LibraryDecisions.DefaultAiPermission)!.State.AiPermissions["team"]);
        Assert.True(LibraryAdoptionPlanner.Plan(catalog, firstStart, onEverywhere)!.State.AiPermissions["team"]);
        Assert.False(LibraryAdoptionPlanner.Plan(catalog, firstStart, offForEveryCustom)!.State.AiPermissions["team"]);

        var healthy = Lib.Catalog(
            Lib.State(enabled: ["github"]),
            Lib.Committed(Lib.BuiltInLibrary("github", Lib.Shipped("get hub", "GitHub"))),
            Lib.Committed(Lib.CustomLibrary("placed", Lib.Custom("kube", "K8s")), Lib.H2));
        Assert.False(LibraryAdoptionPlanner.Plan(healthy, firstStart, LibraryDecisions.DefaultAiPermission)!.State.AiPermissions["placed"]);
        Assert.True(LibraryAdoptionPlanner.Plan(healthy, firstStart, onEverywhere)!.State.AiPermissions["placed"]);

        // The composer's plan is the policy point's plan.
        Assert.Equal(
            LibraryAdoptionPlanner.Plan(catalog, firstStart, LibraryDecisions.DefaultAiPermission)!.State.AiPermissions,
            LibraryComposer.Instance.PlanAdoption(catalog, firstStart)!.State.AiPermissions);
    }

    [Fact]
    public void No_other_code_names_a_precedence_rule()
    {
        // Composition implements each alternative; only the policy point chooses one.
        var core = Path.Combine(RepositoryRoot(), "src", "Scribe.Core");
        var naming = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("LibraryPrecedenceRule.", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["LibraryComposition.cs", "LibraryDecisions.cs"], naming);
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
