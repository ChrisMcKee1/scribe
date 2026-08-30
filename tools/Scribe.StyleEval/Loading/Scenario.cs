using System.Text.Json.Serialization;

namespace Scribe.StyleEval.Corpus;

/// <summary>
/// One selection a user could plausibly have highlighted, plus what a correct transformation of it
/// must and must not do.
/// </summary>
/// <remarks>
/// <para>
/// The expectations come in two halves, and carrying both is the whole point of the schema. The
/// NEGATIVE half (<see cref="ExpectNoBold"/>, <see cref="ExpectNoList"/>,
/// <see cref="ProtectedTokens"/>, <see cref="ContainsDash"/>) catches a model that formatted too
/// much or changed something it may not change. The POSITIVE half (<see cref="ShouldBold"/>,
/// <see cref="ShouldList"/>, <see cref="ShouldTable"/>, <see cref="ShouldHeading"/>,
/// <see cref="ShouldCode"/>, <see cref="SpelledOutNumbers"/>) catches the failure no negative check
/// can see: a model that quietly formats NOTHING passes every restraint ceiling while producing a
/// worse result than a careful human editor.
/// </para>
/// <para>
/// A scenario carrying neither half tests nothing, and <see cref="CorpusLoader"/> says so.
/// </para>
/// </remarks>
internal sealed record Scenario
{
    /// <summary>Stable id, conventionally "category-NNN". Half of the cell key, so never renamed.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Which corpus file family this belongs to; drives <c>--categories</c>.</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>The input text exactly as a user would have selected it on screen.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>Free-form labels describing what is in the text. Reporting and slicing only.</summary>
    [JsonPropertyName("traits")]
    public IReadOnlyList<string> Traits { get; init; } = [];

    /// <summary>Substrings that must survive byte-identical into the output.</summary>
    [JsonPropertyName("protectedTokens")]
    public IReadOnlyList<string> ProtectedTokens { get; init; } = [];

    /// <summary>True when the text deliberately contains U+2014 or U+2013 that must be preserved.</summary>
    [JsonPropertyName("containsDash")]
    public bool ContainsDash { get; init; }

    /// <summary>Spoken number, time and date phrases the house style requires be written in digits.</summary>
    [JsonPropertyName("spelledOutNumbers")]
    public IReadOnlyList<string> SpelledOutNumbers { get; init; } = [];

    /// <summary>
    /// True when the text contains none of the Detection emphasis triggers, so a correct result has
    /// zero bold.
    /// </summary>
    [JsonPropertyName("expectNoBold")]
    public bool ExpectNoBold { get; init; }

    /// <summary>True when the content is one connected argument and a correct result stays prose.</summary>
    [JsonPropertyName("expectNoList")]
    public bool ExpectNoList { get; init; }

    /// <summary>The exact phrases the author's own marker words make bold-eligible.</summary>
    [JsonPropertyName("shouldBold")]
    public IReadOnlyList<string> ShouldBold { get; init; } = [];

    /// <summary>True when three or more genuine peer items survive the reorder test.</summary>
    [JsonPropertyName("shouldList")]
    public bool ShouldList { get; init; }

    /// <summary>True when two or more records share the same fields.</summary>
    [JsonPropertyName("shouldTable")]
    public bool ShouldTable { get; init; }

    /// <summary>
    /// How many records share those fields, where the count is what decides the answer.
    /// </summary>
    /// <remarks>
    /// Two records are a real repeated structure and JSON renders them as an array of two objects,
    /// but they sit below the three-row table floor in <c>EnrichmentRules.Restraint</c>, so the
    /// correct Markdown and HTML answer is paired lines rather than a table. Only the ambiguous case
    /// needs stating: zero means unstated, which the checkers read as three or more.
    /// </remarks>
    [JsonPropertyName("recordCount")]
    public int RecordCount { get; init; }

    /// <summary>True when there are at least two sections of two or more paragraphs each.</summary>
    [JsonPropertyName("shouldHeading")]
    public bool ShouldHeading { get; init; }

    /// <summary>Identifiers, paths, commands, flags and error strings that should get code formatting.</summary>
    [JsonPropertyName("shouldCode")]
    public IReadOnlyList<string> ShouldCode { get; init; } = [];

    /// <summary>One line saying what this scenario tests.</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    /// <summary>Where this scenario was loaded from, for error messages. Not part of the JSON.</summary>
    [JsonIgnore]
    public string SourceFile { get; init; } = string.Empty;

    /// <summary>Line number within <see cref="SourceFile"/>, 1-based. Not part of the JSON.</summary>
    [JsonIgnore]
    public int SourceLine { get; init; }

    /// <summary>True when the scenario asserts at least one thing, either direction.</summary>
    [JsonIgnore]
    public bool HasAnyExpectation =>
        ExpectNoBold || ExpectNoList || ContainsDash ||
        ShouldList || ShouldTable || ShouldHeading ||
        ProtectedTokens.Count > 0 || SpelledOutNumbers.Count > 0 ||
        ShouldBold.Count > 0 || ShouldCode.Count > 0;
}
