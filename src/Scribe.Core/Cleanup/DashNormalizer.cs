using System.Text;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Rewrites em dashes and en dashes out of AI cleanup output.
/// </summary>
/// <remarks>
/// The writing style tells the model not to use them, but instructions are advisory and every model
/// tested ignores them some of the time, so this is the deterministic backstop that makes the house
/// style actually hold. It runs only on the model's answer, never on raw speech (the ASR never emits
/// these characters) and never on dictionary replacements or snippet templates, which are the user's
/// own text and may legitimately contain a dash.
/// </remarks>
public static class DashNormalizer
{
    private const char EmDash = '\u2014';
    private const char EnDash = '\u2013';

    /// <summary>True when <paramref name="value"/> contains an em dash or en dash.</summary>
    public static bool ContainsDash(string? value) =>
        !string.IsNullOrEmpty(value) && value.AsSpan().IndexOfAny(EmDash, EnDash) >= 0;

    /// <summary>
    /// Replaces em/en dashes with the punctuation a careful writer would have used. Returns the input
    /// unchanged when it holds none, so the common path allocates nothing.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (!ContainsDash(value))
        {
            return value ?? string.Empty;
        }

        var source = value!;
        var builder = new StringBuilder(source.Length + 8);

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c != EmDash && c != EnDash)
            {
                builder.Append(c);
                continue;
            }

            // Collapse a run of dashes ("--" typed as two em dashes) into one decision.
            while (i + 1 < source.Length && (source[i + 1] == EmDash || source[i + 1] == EnDash))
            {
                i++;
            }

            var beforeIndex = LastNonSpace(builder);
            var before = beforeIndex >= 0 ? builder[beforeIndex] : '\0';
            var afterIndex = NextNonSpace(source, i + 1);
            var after = afterIndex >= 0 ? source[afterIndex] : '\0';

            // A dash between digits is a range: "pages 3-7", "about 1-2 GB".
            if (char.IsDigit(before) && char.IsDigit(after))
            {
                TrimTrailingSpaces(builder);
                builder.Append(" to ");
                i = afterIndex - 1;
                continue;
            }

            // Line-leading dash: a bullet or a dialogue dash. Drop it rather than open with a comma.
            if (beforeIndex < 0 || before == '\n' || before == '\r')
            {
                SkipSpacesAfterDash(source, ref i);
                continue;
            }

            // Already punctuated on the left ("wait, - I mean"): the dash adds nothing.
            if (IsSentencePunctuation(before))
            {
                TrimTrailingSpaces(builder);
                builder.Append(' ');
                SkipSpacesAfterDash(source, ref i);
                continue;
            }

            // Nothing meaningful follows, or what follows closes the clause rather than continuing it:
            // the dash was trailing, so drop it instead of inventing a comma before ")" or a newline.
            if (afterIndex < 0 || IsClauseClosing(after))
            {
                TrimTrailingSpaces(builder);
                continue;
            }

            TrimTrailingSpaces(builder);
            builder.Append(", ");
            SkipSpacesAfterDash(source, ref i);
        }

        return builder.ToString();
    }

    private static void SkipSpacesAfterDash(string source, ref int i)
    {
        // Consume horizontal space only; a newline is structure the model chose and must survive.
        while (i + 1 < source.Length && (source[i + 1] == ' ' || source[i + 1] == '\t'))
        {
            i++;
        }
    }

    private static void TrimTrailingSpaces(StringBuilder builder)
    {
        while (builder.Length > 0 && (builder[^1] == ' ' || builder[^1] == '\t'))
        {
            builder.Length--;
        }
    }

    private static int LastNonSpace(StringBuilder builder)
    {
        for (var i = builder.Length - 1; i >= 0; i--)
        {
            if (builder[i] != ' ' && builder[i] != '\t')
            {
                return i;
            }
        }

        return -1;
    }

    private static int NextNonSpace(string source, int start)
    {
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] != ' ' && source[i] != '\t')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsSentencePunctuation(char c) =>
        c is ',' or '.' or ';' or ':' or '!' or '?';

    // A comma before one of these would read as a dangling clause ("(aside, )"), so the dash is
    // simply dropped instead. Newlines count: the line break already does the separating work.
    private static bool IsClauseClosing(char c) =>
        c is ')' or ']' or '}' or '"' or '\'' or '\n' or '\r';
}
