namespace Scribe.Core.TextActions;

using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;

/// <summary>
/// Validates and cleans a model's answer before it is allowed anywhere near the user's document.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="TextCleanupService"/>'s <c>TrySanitize</c>, which cannot be
/// reused here for two reasons that both end in corrupting a user's text.
/// </para>
/// <para>
/// <b>Length.</b> <c>TrySanitize</c> applies one ramble bound (2.5x + 80) and no lower bound at all,
/// which is right when output length tracks input length. It does not here: "Make it shorter" and
/// "Rewrite for an AI agent" move in opposite directions, and the missing lower bound would let a
/// 60 character answer silently replace a 320 character selection. Each action declares its own band.
/// </para>
/// <para>
/// <b>Dashes.</b> <see cref="DashNormalizer"/> exists because the ASR never emits an em or en dash, so
/// any dash in cleanup output was invented by the model. Its own remarks say it must never run on the
/// user's own text. A selection <i>is</i> the user's own text: it may legitimately contain an en dash,
/// and <c>DashNormalizer</c> rewrites a dash between digits into the word "to", so proofreading a
/// document containing "pages 3-7" written with an en dash would silently change the user's content
/// with nothing in the diff to explain it. The rule that actually holds for a transform is that the
/// output may not contain <b>more</b> dashes than the input.
/// </para>
/// </remarks>
public static class TextActionSanitizer
{
    private static readonly Regex ThinkBlock =
        new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Absolute ceiling regardless of action, to catch a runaway generation.</summary>
    private const double AbsoluteMaxRatio = 12.0;

    /// <summary>
    /// Shortest selection for which the ratio bands mean anything. Below this, structural conversion
    /// legitimately multiplies the length many times over and a ratio test only produces false
    /// rejections.
    /// </summary>
    private const int RatioFloorChars = 80;

    /// <summary>
    /// Length below which no answer is ever a runaway, so a short selection is never rejected purely
    /// for growing. Roughly a long paragraph.
    /// </summary>
    private const int AbsoluteMinChars = 600;

    /// <summary>Why a candidate answer was rejected. Surfaced to the user, so keep the names meaningful.</summary>
    public enum RejectionReason
    {
        /// <summary>Accepted; not a rejection.</summary>
        None,

        /// <summary>The model returned nothing usable.</summary>
        Empty,

        /// <summary>The answer was far shorter than the action allows, which means content was dropped.</summary>
        TooShort,

        /// <summary>The answer was far longer than the action allows, which usually means the model rambled.</summary>
        TooLong,

        /// <summary>The model declined the request instead of transforming the text.</summary>
        Refused,

        /// <summary>The model added em or en dashes that the selection did not contain.</summary>
        AddedDashes,

        /// <summary>A JSON conversion returned something that does not parse.</summary>
        InvalidJson,
    }

    private static bool ParsesAsJson(string candidate)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(candidate);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Outcome of validating one model answer.</summary>
    /// <param name="Accepted">True when <paramref name="Text"/> is safe to show the user.</param>
    /// <param name="Text">The cleaned answer on success, or the original selection on rejection.</param>
    /// <param name="Reason">Why it was rejected, or <see cref="RejectionReason.None"/>.</param>
    public readonly record struct SanitizeResult(bool Accepted, string Text, RejectionReason Reason);

    /// <summary>
    /// Cleans and validates <paramref name="candidate"/> against the action's contract. Never throws.
    /// On rejection the original selection is returned, so a caller that ignores
    /// <see cref="SanitizeResult.Accepted"/> still cannot corrupt the document.
    /// </summary>
    public static SanitizeResult Sanitize(string? candidate, string original, TextAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        original ??= string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return new SanitizeResult(false, original, RejectionReason.Empty);
        }

        var cleaned = ThinkBlock.Replace(candidate, string.Empty).Trim();
        cleaned = StripWrappingFence(cleaned, action);
        cleaned = StripEchoedTags(cleaned);
        cleaned = StripWrappingQuotes(cleaned, original);

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new SanitizeResult(false, original, RejectionReason.Empty);
        }

        // Refusal check before the length bands: a refusal is short, so it would otherwise be reported
        // as TooShort, which tells the user nothing about what went wrong.
        if (TextCleanupService.LooksLikeRefusal(cleaned) && !TextCleanupService.LooksLikeRefusal(original))
        {
            return new SanitizeResult(false, original, RejectionReason.Refused);
        }

        // A ratio test is meaningless on a very short selection, but ONLY for a structural
        // conversion. "bob 3pm sue 4pm" is 15 characters, and a correct JSON conversion of it, with
        // inferred keys, quoting and explicit nulls, runs well past 100. That would be rejected as a
        // ramble, telling the user the model misbehaved when it did exactly the right thing.
        //
        // The exemption is deliberately NOT extended to the other bands. A tone rewrite or a
        // shortening of a short selection has no reason to multiply in size, so those keep their
        // ratio check at every length: a 400 character answer to an 11 character input is a ramble
        // whatever the input length.
        var exemptFromRatio =
            action.Length == TextActionLength.Restructure && original.Length < RatioFloorChars;

        if (!exemptFromRatio)
        {
            var ratio = (double)cleaned.Length / original.Length;
            var (min, max) = Bounds(action.Length);

            if (ratio < min)
            {
                return new SanitizeResult(false, original, RejectionReason.TooShort);
            }

            if (ratio > max)
            {
                return new SanitizeResult(false, original, RejectionReason.TooLong);
            }
        }

        // The runaway backstop. Applies at every length, and on an exempt short selection it is the
        // only size check standing, so it is what stops a 5,000 character answer to "bob 3pm".
        if (cleaned.Length > Math.Max(AbsoluteMinChars, original.Length * AbsoluteMaxRatio))
        {
            return new SanitizeResult(false, original, RejectionReason.TooLong);
        }

        // A JSON conversion that does not parse is worse than no answer: the palette makes Replace
        // the primary button, so an unnoticed failure is one Enter away from landing in the document.
        if (action.Id == "format-json" && !ParsesAsJson(cleaned))
        {
            return new SanitizeResult(false, original, RejectionReason.InvalidJson);
        }

        // The house style still holds for text the model wrote, but only for dashes it INTRODUCED.
        // Preserving the user's own dashes is the whole difference between this and cleanup.
        if (CountDashes(cleaned) > CountDashes(original))
        {
            var normalized = DashNormalizer.Normalize(cleaned);

            // Normalizing can only remove dashes, so if the count still exceeds the original the
            // remainder came from the user's own text surviving into the answer, which is correct.
            cleaned = CountDashes(normalized) <= CountDashes(original) ? normalized : cleaned;
        }

        return new SanitizeResult(true, cleaned, RejectionReason.None);
    }

    /// <summary>The allowed output-to-input length ratio for each policy.</summary>
    internal static (double Min, double Max) Bounds(TextActionLength length) => length switch
    {
        TextActionLength.Similar => (0.40, 2.00),
        TextActionLength.Shorter => (0.10, 1.10),
        TextActionLength.Longer => (0.80, 6.00),
        // A conversion can legitimately shrink (prose to a task list) or balloon (prose to HTML), so
        // only the runaway ceiling applies.
        _ => (0.05, AbsoluteMaxRatio),
    };

    /// <summary>A human-readable reason for the palette to show when an answer is rejected.</summary>
    public static string Describe(RejectionReason reason) => reason switch
    {
        RejectionReason.Empty => "The model returned nothing. Your text was left alone.",
        RejectionReason.TooShort => "The result dropped too much of your text, so Scribe discarded it.",
        RejectionReason.TooLong => "The model rambled instead of rewriting, so Scribe discarded it.",
        RejectionReason.Refused => "The model declined this request. Your text was left alone.",
        RejectionReason.AddedDashes => "The result did not match the house style, so Scribe discarded it.",
        RejectionReason.InvalidJson => "The model did not return valid JSON, so Scribe kept your text.",
        _ => string.Empty,
    };

    private static int CountDashes(string value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c is '—' or '–')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Language tags that mark a fence as a WRAPPER (the model presenting its answer as a document)
    /// rather than as content the answer legitimately contains.
    /// </summary>
    private static readonly string[] WrapperInfoStrings =
        ["", "markdown", "md", "html", "json", "text", "txt", "plaintext"];

    // Decides wrapper versus content by the fence's INFO STRING, not by whether the body happens to
    // contain another fence.
    //
    // The previous heuristic was wrong in both directions, and the JSON case was a data-corruption
    // path rather than a cosmetic bug. A ```json wrapper whose string values contained backticks
    // passed the "body contains a fence" test and survived untouched, and with no JSON validation
    // anywhere the unparseable text was returned as a success and the palette focused Replace, so the
    // next Enter committed it into the user's document. In the other direction a legitimately
    // all-code answer had its opening fence and language tag deleted.
    //
    // The instructions now tell the model to always tag a content fence and never tag a wrapper, so
    // the info string is a contract rather than a guess.
    private static string StripWrappingFence(string value, TextAction action)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal) ||
            !value.EndsWith("```", StringComparison.Ordinal) ||
            value.Length < 7)
        {
            return value;
        }

        var firstNewline = value.IndexOf('\n');
        if (firstNewline < 0)
        {
            return value;
        }

        var info = value[3..firstNewline].Trim();

        // A tagged fence around a code answer is content; the language tag is part of the result.
        // Only an untagged fence, or one tagged with the destination's own name, is a wrapper.
        if (!WrapperInfoStrings.Contains(info, StringComparer.OrdinalIgnoreCase))
        {
            return value;
        }

        return value[(firstNewline + 1)..^3].Trim();
    }

    private static string StripEchoedTags(string value)
    {
        var result = value;
        if (result.StartsWith(TextActionPrompt.SelectionOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            result = result[TextActionPrompt.SelectionOpenTag.Length..].TrimStart();
        }

        if (result.EndsWith(TextActionPrompt.SelectionCloseTag, StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^TextActionPrompt.SelectionCloseTag.Length].TrimEnd();
        }

        return result;
    }

    // Only unwrap when the ORIGINAL was not itself quoted, so transforming a quotation keeps its marks.
    private static string StripWrappingQuotes(string value, string original)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var quoted = (value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'');
        if (!quoted)
        {
            return value;
        }

        var originalQuoted = original.Length >= 2 &&
            ((original[0] == '"' && original[^1] == '"') || (original[0] == '\'' && original[^1] == '\''));

        return originalQuoted ? value : value[1..^1].Trim();
    }
}
