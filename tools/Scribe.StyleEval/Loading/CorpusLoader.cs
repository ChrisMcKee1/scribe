using System.Text;
using System.Text.Json;

namespace Scribe.StyleEval.Corpus;

/// <summary>Raised when a corpus file cannot be trusted. Names the file and the line, always.</summary>
internal sealed class CorpusException(string message) : Exception(message);

/// <summary>
/// Reads <c>corpus/*.jsonl</c> into <see cref="Scenario"/> records: one JSON object per line, no
/// wrapping array.
/// </summary>
/// <remarks>
/// Fails loudly rather than skipping. A silently dropped scenario is the worst outcome an eval
/// corpus can have, because the run still reports a pass rate and the missing coverage is invisible
/// in the numbers. Every rejection carries the file, the line, and what was wrong with it.
/// </remarks>
internal static class CorpusLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Loads every <c>*.jsonl</c> in <paramref name="directory"/>, ordered by id.</summary>
    /// <param name="directory">The corpus directory.</param>
    /// <param name="advisories">
    /// Things worth a human's attention that are not grounds for rejecting the corpus. Kept separate
    /// from the exceptions on purpose: a hard failure has to mean the corpus is untrustworthy, and if
    /// it also means "this looks slightly unusual" nobody will leave the strictness turned on.
    /// </param>
    public static IReadOnlyList<Scenario> Load(string directory, out IReadOnlyList<string> advisories)
    {
        var notes = new List<string>();
        advisories = notes;
        if (!Directory.Exists(directory))
        {
            throw new CorpusException($"Corpus directory not found: {directory}");
        }

        var files = Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            throw new CorpusException($"No .jsonl files in the corpus directory: {directory}");
        }

        var all = new List<Scenario>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (var scenario in LoadFile(file, notes))
            {
                if (seen.TryGetValue(scenario.Id, out var first))
                {
                    throw new CorpusException(
                        $"{Short(file)}:{scenario.SourceLine}: duplicate scenario id '{scenario.Id}', " +
                        $"already defined at {first}. Ids are half the cell key, so they must be unique.");
                }

                seen[scenario.Id] = $"{Short(file)}:{scenario.SourceLine}";
                all.Add(scenario);
            }
        }

        return [.. all.OrderBy(s => s.Category, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    private static IEnumerable<Scenario> LoadFile(string file, List<string> advisories)
    {
        var expectedCategory = Path.GetFileNameWithoutExtension(file);
        var lineNumber = 0;

        foreach (var rawLine in File.ReadLines(file, Encoding.UTF8))
        {
            lineNumber++;
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') || line.StartsWith(']'))
            {
                throw new CorpusException(
                    $"{Short(file)}:{lineNumber}: the corpus is JSON Lines, one object per line. " +
                    "Remove the wrapping array.");
            }

            Scenario? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Scenario>(line, Json);
            }
            catch (JsonException ex)
            {
                throw new CorpusException($"{Short(file)}:{lineNumber}: malformed JSON. {ex.Message}");
            }

            if (parsed is null)
            {
                throw new CorpusException($"{Short(file)}:{lineNumber}: the line parsed to null.");
            }

            var scenario = parsed with { SourceFile = file, SourceLine = lineNumber };
            Validate(scenario, expectedCategory, advisories);
            yield return scenario;
        }
    }

    private static void Validate(Scenario s, string expectedCategory, List<string> advisories)
    {
        var where = $"{Short(s.SourceFile)}:{s.SourceLine}";

        if (string.IsNullOrWhiteSpace(s.Id))
        {
            throw new CorpusException($"{where}: 'id' is required.");
        }

        if (string.IsNullOrWhiteSpace(s.Category))
        {
            throw new CorpusException($"{where}: 'category' is required.");
        }

        if (!string.Equals(s.Category, expectedCategory, StringComparison.Ordinal))
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' declares category '{s.Category}' but lives in " +
                $"{expectedCategory}.jsonl. The file name is the category.");
        }

        if (!s.Id.StartsWith(s.Category + "-", StringComparison.Ordinal))
        {
            throw new CorpusException(
                $"{where}: id '{s.Id}' must start with '{s.Category}-' so a result row identifies " +
                "its category without a lookup.");
        }

        if (string.IsNullOrWhiteSpace(s.Text))
        {
            throw new CorpusException($"{where}: scenario '{s.Id}' has no 'text' to transform.");
        }

        if (!s.HasAnyExpectation)
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' carries no expectation of either kind, so it tests " +
                "nothing. Give it a negative guard (expectNoBold, expectNoList, protectedTokens, " +
                "containsDash) or a positive one (shouldBold, shouldList, shouldTable, " +
                "shouldHeading, shouldCode, spelledOutNumbers).");
        }

        foreach (var token in s.ProtectedTokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new CorpusException($"{where}: scenario '{s.Id}' has an empty protectedToken.");
            }

            if (!s.Text.Contains(token, StringComparison.Ordinal))
            {
                throw new CorpusException(
                    $"{where}: scenario '{s.Id}' protects '{token}', which does not appear in its " +
                    "own text. A token the input never contained can never be preserved.");
            }
        }

        foreach (var phrase in s.SpelledOutNumbers)
        {
            if (!ContainsLoose(s.Text, phrase))
            {
                throw new CorpusException(
                    $"{where}: scenario '{s.Id}' expects '{phrase}' to be converted to digits, but " +
                    "the phrase does not appear in its own text.");
            }
        }

        var actualDashes = s.Text.Any(c => c is '—' or '–');
        if (s.ContainsDash != actualDashes)
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' sets containsDash={s.ContainsDash.ToString().ToLowerInvariant()} " +
                $"but its text {(actualDashes ? "does" : "does not")} contain an em or en dash. " +
                "Dash metadata has to match the text, because a whole checker keys off it.");
        }

        if (s.ExpectNoBold && s.ShouldBold.Count > 0)
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' both forbids bold and requires it. Pick one.");
        }

        if (s.RecordCount is not 0 and < 2)
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' sets recordCount={s.RecordCount}. One record is not a " +
                "repeated structure; leave the field out unless the count is exactly what decides " +
                "the answer.");
        }

        if (s.RecordCount > 0 && !s.ShouldTable)
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' sets recordCount without shouldTable. The count only " +
                "means anything for a scenario whose records share the same fields.");
        }

        if (s.ExpectNoList && s.ShouldList)
        {
            throw new CorpusException(
                $"{where}: scenario '{s.Id}' both forbids a list and requires one. Pick one.");
        }

        foreach (var phrase in s.ShouldBold)
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                throw new CorpusException($"{where}: scenario '{s.Id}' has an empty shouldBold phrase.");
            }
        }

        // shouldCode names the token as it should appear in the OUTPUT, which is not always how it
        // appears in the input. A dictated selection says "branch release slash one point four" and
        // the house style writes that as release/1.4, so requiring literal containment here would
        // reject a correct scenario. Absence is still worth surfacing, because a typo looks
        // identical, so it is collected as an advisory rather than thrown.
        foreach (var token in s.ShouldCode.Where(t => !s.Text.Contains(t, StringComparison.Ordinal)))
        {
            advisories.Add(
                $"{where}: scenario '{s.Id}' expects '{token}' in code formatting, and the token does " +
                "not appear literally in its own text. Fine for a dictated form the house style " +
                "rewrites; a typo otherwise.");
        }
    }

    /// <summary>Whitespace-insensitive, case-insensitive containment, for spoken phrases.</summary>
    internal static bool ContainsLoose(string haystack, string needle)
    {
        static string Squash(string value)
        {
            var sb = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        return Squash(haystack).Contains(Squash(needle), StringComparison.Ordinal);
    }

    private static string Short(string file) =>
        Path.Combine(Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty, Path.GetFileName(file));
}
