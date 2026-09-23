using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.AsrCheck.Scenarios;

/// <summary>
/// The test dictionary and snippets the suite loads into its temporary database. Patterns cover
/// the spellings the recogniser plausibly emits for each spoken term, which is how a real user's
/// dictionary is built: from the misrecognitions they actually see.
/// </summary>
internal static class TestVocabulary
{
    public const string SignaturePhrase = "insert my signature";
    public const string SignatureTemplate = "Kind regards,\nsent from scribe";

    // The template itself passes through the dictionary after expansion, so "scribe" in it comes
    // out canonical. That ordering (snippets first, then dictionary) is part of the contract.
    public const string SignatureCanonical = "Kind regards,\nsent from Scribe";

    public const string AddressPhrase = "insert my address";
    public const string AddressTemplate = "1 Example Street\nSpringfield";

    /// <summary>
    /// The first two spellings of each term are the plausible ones; the third ("devos", "gethub",
    /// "cuba ernets") is what the bundled recogniser actually produced for these fixtures on x64,
    /// which is exactly the entry a user adds after seeing it once. Canonical-term presence is
    /// reported, never asserted, because another build or architecture may mishear differently.
    /// </summary>
    public static IReadOnlyList<DictionaryEntry> Dictionary { get; } =
    [
        DictionaryEntry.New("azure devops", "Azure DevOps"),
        DictionaryEntry.New("azure dev ops", "Azure DevOps"),
        DictionaryEntry.New("azure devos", "Azure DevOps"),
        DictionaryEntry.New("github copilot", "GitHub Copilot"),
        DictionaryEntry.New("github co-pilot", "GitHub Copilot"),
        DictionaryEntry.New("git hub copilot", "GitHub Copilot"),
        DictionaryEntry.New("gethub copilot", "GitHub Copilot"),
        DictionaryEntry.New("kubernetes", "Kubernetes"),
        DictionaryEntry.New("cuba ernets", "Kubernetes"),
        DictionaryEntry.New("scribe", "Scribe"),
    ];

    public static IReadOnlyList<Snippet> Snippets { get; } =
    [
        Snippet.New(SignaturePhrase, SignatureTemplate),
        Snippet.New(AddressPhrase, AddressTemplate),
    ];

    /// <summary>What each snippet trigger must expand to, after dictionary canonicalization.</summary>
    public static IReadOnlyDictionary<string, string> SnippetOutputs { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["snippet-signature"] = SignatureCanonical,
        ["snippet-address"] = AddressTemplate,
    };

    public static IReadOnlyDictionary<string, string> SnippetTriggers { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["snippet-signature"] = SignaturePhrase,
        ["snippet-address"] = AddressPhrase,
    };

    /// <summary>The canonical form each dictionary phrase is about, for reporting.</summary>
    public static IReadOnlyDictionary<string, string> DictionaryTerms { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dict-azure-devops"] = "Azure DevOps",
        ["dict-scribe"] = "Scribe",
        ["dict-github-copilot"] = "GitHub Copilot",
        ["dict-kubernetes"] = "Kubernetes",
    };

    /// <summary>
    /// Recogniser-independent cases with literal expectations, including every replacement record.
    /// They pin the post-processor on the exact call shape the controller uses without AI cleanup,
    /// <c>ProcessDetailed(text, text)</c>, so a change to matching or to highlight metadata shows up
    /// here even when no WAV happens to exercise it.
    /// </summary>
    public static IReadOnlyList<PostProcessingExpectation> SourceCases { get; } =
    [
        Case("dictionary phrase",
            "we track every bug in azure devops and review the board.",
            "we track every bug in Azure DevOps and review the board.",
            ("azure devops", "Azure DevOps", TextReplacementKind.Dictionary)),
        Case("two variants in one sentence",
            "azure dev ops and git hub copilot",
            "Azure DevOps and GitHub Copilot",
            ("azure dev ops", "Azure DevOps", TextReplacementKind.Dictionary),
            ("git hub copilot", "GitHub Copilot", TextReplacementKind.Dictionary)),
        Case("hyphenated variant",
            "ask github co-pilot why the build is failing.",
            "ask GitHub Copilot why the build is failing.",
            ("github co-pilot", "GitHub Copilot", TextReplacementKind.Dictionary)),
        Case("single word at sentence start",
            "kubernetes restarted overnight.",
            "Kubernetes restarted overnight.",
            ("kubernetes", "Kubernetes", TextReplacementKind.Dictionary)),
        Case("already canonical text records nothing",
            "Scribe types whatever I say.",
            "Scribe types whatever I say."),
        Case("whole words only",
            "the scribes are kubernetesy.",
            "the scribes are kubernetesy."),
        Case("snippet with trailing punctuation, template canonicalized",
            "Insert my signature.",
            SignatureCanonical,
            (SignaturePhrase, SignatureCanonical, TextReplacementKind.Snippet)),
        Case("snippet",
            "insert my address",
            AddressTemplate,
            (AddressPhrase, AddressTemplate, TextReplacementKind.Snippet)),
        Case("whitespace normalization",
            "  kubernetes   is up ,  finally  ",
            "Kubernetes is up, finally",
            ("kubernetes", "Kubernetes", TextReplacementKind.Dictionary)),
    ];

    private static PostProcessingExpectation Case(
        string name,
        string input,
        string expected,
        params (string Pattern, string Replacement, TextReplacementKind Kind)[] records) =>
        new(name, input, expected, records.Select(r =>
        {
            var start = expected.IndexOf(r.Replacement, StringComparison.Ordinal);
            return new ReplacementInfo(start, r.Replacement.Length, r.Pattern, r.Replacement, r.Kind.ToString());
        }).ToList());
}

internal sealed record PostProcessingExpectation(
    string Name, string Input, string ExpectedText, IReadOnlyList<ReplacementInfo> ExpectedReplacements);

internal sealed record PostProcessingCase(
    string Name,
    string Input,
    string ExpectedText,
    string ActualText,
    IReadOnlyList<ReplacementInfo> ExpectedReplacements,
    IReadOnlyList<ReplacementInfo> ActualReplacements,
    double ElapsedMs,
    CheckStatus Status,
    string Detail);

/// <summary>Post-processing checks that do not need a recogniser, plus the per-scenario checks that do.</summary>
internal static class PostProcessingChecks
{
    public static IEnumerable<PostProcessingCase> RunSourceCases(ITextPostProcessor processor)
    {
        foreach (var expectation in TestVocabulary.SourceCases)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = processor.ProcessDetailed(expectation.Input, expectation.Input);
            timer.Stop();
            var actual = Records(result);
            var textMatches = string.Equals(result.Text, expectation.ExpectedText, StringComparison.Ordinal);
            var recordsMatch = actual.SequenceEqual(expectation.ExpectedReplacements);
            yield return new PostProcessingCase(
                expectation.Name,
                expectation.Input,
                expectation.ExpectedText,
                result.Text,
                expectation.ExpectedReplacements,
                actual,
                Math.Round(timer.Elapsed.TotalMilliseconds, 3),
                textMatches && recordsMatch ? CheckStatus.Pass : CheckStatus.Fail,
                textMatches
                    ? recordsMatch ? string.Empty : "replacement records differ from the expected records"
                    : "final text differs from the expected text");
        }
    }

    public static List<ReplacementInfo> Records(TextPostProcessingResult result) =>
        result.Replacements
            .Select(r => new ReplacementInfo(r.Start, r.Length, r.Pattern, r.Replacement, r.Kind.ToString()))
            .ToList();

    /// <summary>Checks one decoded scenario's post-processing result.</summary>
    public static IEnumerable<CheckResult> ForDecode(string? baseClip, string decoded, TextPostProcessingResult result)
    {
        var records = result.Replacements;

        // Every highlight must point at the text it claims to have written. A misplaced span is
        // exactly the metadata drift a matching optimization can introduce while the text stays right.
        var misplaced = records
            .Where(r => r.Start < 0 || r.Start + r.Length > result.Text.Length
                || !string.Equals(result.Text.Substring(r.Start, r.Length), r.Replacement, StringComparison.OrdinalIgnoreCase))
            .ToList();
        yield return misplaced.Count == 0
            ? new CheckResult("post-processing: replacement spans", CheckStatus.Pass, string.Empty)
            : new CheckResult("post-processing: replacement spans", CheckStatus.Fail,
                $"{misplaced.Count} record(s) do not cover their replacement text");

        if (!records.Any(r => r.Kind == TextReplacementKind.Snippet))
        {
            // With no snippet involved and a dictionary whose entries never overlap, applying the
            // entries one at a time through the production single-rule entry point must give the
            // same text as the full pass.
            var expected = TestVocabulary.Dictionary.Aggregate(decoded, (text, entry) => TextPostProcessor.ApplyRule(text, entry));
            yield return string.Equals(expected, result.Text, StringComparison.Ordinal)
                ? new CheckResult("post-processing: dictionary text", CheckStatus.Pass, string.Empty)
                : new CheckResult("post-processing: dictionary text", CheckStatus.Fail,
                    $"full pass produced \"{result.Text}\" but the single-rule path produced \"{expected}\"");
        }

        if (baseClip is not null && TestVocabulary.SnippetTriggers.TryGetValue(baseClip, out var trigger))
        {
            var canonical = TestVocabulary.SnippetOutputs[baseClip];
            if (NormalizeUtterance(decoded) == trigger)
            {
                var expanded = string.Equals(result.Text, canonical, StringComparison.Ordinal)
                    && records.Any(r => r.Kind == TextReplacementKind.Snippet && r.Replacement == canonical);
                yield return new CheckResult("post-processing: snippet expansion", expanded ? CheckStatus.Pass : CheckStatus.Fail,
                    expanded ? string.Empty : $"trigger was recognized but the result was \"{Escape(result.Text)}\"");
            }
            else
            {
                yield return new CheckResult("post-processing: snippet expansion", CheckStatus.Report,
                    $"the recognizer produced \"{decoded}\", not the bare trigger, so the snippet was not asserted");
            }
        }

        if (baseClip is not null && TestVocabulary.DictionaryTerms.TryGetValue(baseClip, out var term))
        {
            var present = result.Text.Contains(term, StringComparison.Ordinal);
            yield return new CheckResult("post-processing: canonical term", CheckStatus.Report,
                present
                    ? $"\"{term}\" present ({records.Count(r => r.Kind == TextReplacementKind.Dictionary)} dictionary record(s))"
                    : $"\"{term}\" absent: the recognizer produced a spelling the test dictionary does not map");
        }
    }

    private static string NormalizeUtterance(string text) =>
        string.Join(' ', text.Trim().TrimEnd('.', '!', '?', ',', ';', ':').ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Escape(string text) => text.Replace("\n", "\\n", StringComparison.Ordinal);
}
