using Scribe.Core.TextActions;
using Scribe.StyleEval.Checks;
using Scribe.StyleEval.Corpus;

namespace Scribe.StyleEval.Tests;

/// <summary>
/// Builds one scenario-plus-action cell with a known right answer.
/// </summary>
/// <remarks>
/// Every fixture goes through the shipping <see cref="TextActionCatalog"/> rather than a stub
/// action. The gates that decide whether a checker fires at all (the destination, the length band
/// and the enrichment level) are read off the catalog, so a stubbed action would let the fixtures
/// agree with each other while disagreeing with the thing that actually ships.
/// </remarks>
internal static class Fixture
{
    public static TextAction Action(string id) =>
        TextActionCatalog.Find(id) ?? throw new InvalidOperationException($"no shipping action '{id}'");

    /// <summary>A scenario carrying only the expectations one test needs.</summary>
    public static Scenario Selection(
        string text,
        IReadOnlyList<string>? protectedTokens = null,
        IReadOnlyList<string>? spelledOutNumbers = null,
        bool containsDash = false,
        bool expectNoBold = false,
        bool expectNoList = false,
        IReadOnlyList<string>? shouldBold = null,
        bool shouldList = false,
        bool shouldTable = false,
        int recordCount = 0,
        IReadOnlyList<string>? shouldCode = null) =>
        new()
        {
            Id = "fixture-001",
            Category = "fixture",
            Text = text,
            ProtectedTokens = protectedTokens ?? [],
            SpelledOutNumbers = spelledOutNumbers ?? [],
            ContainsDash = containsDash,
            ExpectNoBold = expectNoBold,
            ExpectNoList = expectNoList,
            ShouldBold = shouldBold ?? [],
            ShouldList = shouldList,
            ShouldTable = shouldTable,
            RecordCount = recordCount,
            ShouldCode = shouldCode ?? [],
        };

    public static CheckContext Cell(string actionId, Scenario scenario, string output) =>
        new(scenario, Action(actionId), output, sanitizerAccepted: true);

    public static CheckContext Cell(TextAction action, Scenario scenario, string output) =>
        new(scenario, action, output, sanitizerAccepted: true);
}

/// <summary>
/// Verdict assertions that put the checker's own reason string into the failure message.
/// </summary>
/// <remarks>
/// A bare status comparison tells you a checker disagreed and nothing about why. The reason is the
/// only thing that distinguishes "the fixture is wrong" from "the checker is wrong", which is the
/// entire question these tests exist to answer.
/// </remarks>
internal static class Expect
{
    public static void Pass(CheckResult result) =>
        Assert.True(result.Status == CheckStatus.Pass,
            $"{result.Check}: expected Pass, got {result.Status}. Reason: {result.Reason}");

    public static void Fail(CheckResult result, string? reasonContains = null)
    {
        Assert.True(result.Status == CheckStatus.Fail,
            $"{result.Check}: expected Fail, got {result.Status}. Reason: {result.Reason}");

        if (reasonContains is not null)
        {
            Assert.Contains(reasonContains, result.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void NotApplicable(CheckResult result) =>
        Assert.True(result.Status == CheckStatus.NotApplicable,
            $"{result.Check}: expected NotApplicable, got {result.Status}. Reason: {result.Reason}");
}
