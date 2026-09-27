using System.Text;
using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// Reads the log calls out of C# source for the log-safety guards: each call's top-level
/// arguments, found with balanced brackets and with string, verbatim, interpolated, raw and char
/// literals and comments skipped, so a comma or parenthesis inside text never splits an argument.
/// Deliberately small: it understands exactly enough C# to judge a logging call, nothing more.
/// </summary>
internal static partial class LogCallScanner
{
    internal sealed record LogCall(string Method, IReadOnlyList<string> Arguments, string Text);

    internal sealed record Offence(string Call, string Reason);

    [GeneratedRegex(@"\.(?<method>Log(?:Trace|Debug|Information|Warning|Error|Critical)?)\s*\(")]
    private static partial Regex CallStart();

    // The app shell's own logging helpers (TryLog, TryLogTo and the like), called unqualified. What
    // they are handed reaches the log through their bodies, which are ordinary log calls.
    [GeneratedRegex(@"(?<![\w.])(?<method>TryLog\w*)\s*\(")]
    private static partial Regex HelperCallStart();

    // The parameter names a forwarding helper gives its template and its values; anything else in
    // those positions, an exception under another name included, is judged as an ordinary call.
    private static readonly HashSet<string> ForwardedTemplates = new(StringComparer.Ordinal) { "message", "template" };

    private static readonly HashSet<string> ForwardedValues = new(StringComparer.Ordinal) { "args", "values" };

    private static readonly HashSet<string> DeclarationWords = new(StringComparer.Ordinal)
    {
        "void", "bool", "string", "int", "Task", "static", "private", "public", "internal", "protected",
    };

    // Declarations of anything exception-typed: catch variables, parameters, locals.
    [GeneratedRegex(@"\b\w*Exception\??\s+(?<name>[A-Za-z_]\w*)\s*(?:[),;=]|when\b)")]
    private static partial Regex ExceptionDeclaration();

    [GeneratedRegex(@"\.(?:Message|StackTrace|InnerException|InnerExceptions)\b")]
    private static partial Regex ExceptionText();

    [GeneratedRegex(@"\.ToString\s*\(")]
    private static partial Regex ToStringCall();

    [GeneratedRegex(@"^(?:[A-Za-z_][\w.]*\.)?(?:Exception|InnerException)$")]
    private static partial Regex ExceptionMember();

    // What an exception carries in Data, as ex.Data["key"] or ex?.Data.Keys: the target is the identifier chain
    // before it, so a casted or bracketed exception, "((Exception)x).Data", is found too.
    [GeneratedRegex(@"(?<![\w.])(?<target>[A-Za-z_][\w.]*?)\s*[?!]?\s*\)*\s*\.\s*Data\b")]
    private static partial Regex ExceptionData();

    // "(object)", "(object?)", "(System.Exception)": a type in parentheses directly before its operand.
    [GeneratedRegex(@"^\(\s*[A-Za-z_][\w.]*(?:<[\w.,\s<>?]*>)?\??\s*\)(?=\s*[A-Za-z_(@])")]
    private static partial Regex LeadingCast();

    [GeneratedRegex(@"^(?<operand>.+?)\s+as\s+[A-Za-z_][\w.]*(?:<[\w.,\s<>?]*>)?\??$")]
    private static partial Regex TrailingAs();

    public static IEnumerable<LogCall> Find(string source)
    {
        foreach (Match match in CallStart().Matches(source))
        {
            var open = match.Index + match.Length - 1;
            if (TryReadArguments(source, open, out var arguments, out var close))
            {
                yield return new LogCall(match.Groups["method"].Value, arguments, source[match.Index..(close + 1)]);
            }
        }
    }

    /// <summary>Calls to the logging helpers (<c>TryLog(...)</c>), leaving out their own declarations.</summary>
    public static IEnumerable<LogCall> FindHelperCalls(string source)
    {
        foreach (Match match in HelperCallStart().Matches(source))
        {
            if (DeclarationWords.Contains(WordBefore(source, match.Index)))
            {
                continue;
            }

            var open = match.Index + match.Length - 1;
            if (TryReadArguments(source, open, out var arguments, out var close))
            {
                yield return new LogCall(match.Groups["method"].Value, arguments, source[match.Index..(close + 1)]);
            }
        }
    }

    // The identifier ending just before `index`, whitespace skipped: "void" in "private void TryLog(".
    private static string WordBefore(string source, int index)
    {
        var end = index;
        while (end > 0 && char.IsWhiteSpace(source[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] == '_'))
        {
            start--;
        }

        return source[start..end];
    }

    /// <summary>What is wrong with each log call in <paramref name="source"/>, if anything.</summary>
    public static IReadOnlyList<Offence> Check(string source)
    {
        var exceptionNames = new HashSet<string>(StringComparer.Ordinal) { "ex", "exception" };
        foreach (Match declaration in ExceptionDeclaration().Matches(source))
        {
            exceptionNames.Add(declaration.Groups["name"].Value);
        }

        var offences = new List<Offence>();
        foreach (var call in Find(source))
        {
            foreach (var reason in Judge(call, exceptionNames))
            {
                offences.Add(new Offence(Collapse(call.Text), reason));
            }
        }

        // A helper may be handed the exception itself, which its body renders by shape; what it must
        // never be handed is the exception's text, because that reaches the log as an ordinary value.
        foreach (var call in FindHelperCalls(source))
        {
            foreach (var reason in JudgeText(call.Arguments, exceptionNames).Concat(JudgeForwarded(call.Arguments, exceptionNames)))
            {
                offences.Add(new Offence(Collapse(call.Text), reason));
            }
        }

        offences.AddRange(CheckFileOperationLines(source));
        return offences;
    }

    /// <summary>The library journal's failure line, whose two values are the only ones it may carry (review finding G8).</summary>
    internal const string FileOperationTemplate = "\"Library file operation {Operation} failed: {Failure}\"";

    // A LibraryFileOperation-typed parameter or local, the one kind of variable {Operation} may name.
    [GeneratedRegex(@"\bLibraryFileOperation\??\s+(?<name>[A-Za-z_]\w*)\s*[),;=]")]
    private static partial Regex FileOperationDeclaration();

    [GeneratedRegex(@"^LibraryFileOperation\.[A-Za-z_]\w*$")]
    private static partial Regex FileOperationValue();

    [GeneratedRegex(@"^FailureShape\.Describe\s*\(.*\)$", RegexOptions.Singleline)]
    private static partial Regex FailureShapeValue();

    /// <summary>
    /// Every call, direct or through a helper, that writes <see cref="FileOperationTemplate"/> must hand it exactly two
    /// values: a <c>LibraryFileOperation</c> (a member of the enum, or a variable declared with that type) and
    /// <c>FailureShape.Describe(...)</c>. Anything else there, a path, a file name or an id above all, is an offence.
    /// </summary>
    internal static IReadOnlyList<Offence> CheckFileOperationLines(string source)
    {
        var operationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match declaration in FileOperationDeclaration().Matches(source))
        {
            operationNames.Add(declaration.Groups["name"].Value);
        }

        var offences = new List<Offence>();
        foreach (var call in Find(source).Concat(FindHelperCalls(source)))
        {
            var template = -1;
            for (var i = 0; i < call.Arguments.Count && template < 0; i++)
            {
                if (call.Arguments[i] == FileOperationTemplate)
                {
                    template = i;
                }
            }

            if (template < 0)
            {
                continue;
            }

            var values = call.Arguments.Skip(template + 1).Select(Unwrap).ToList();
            if (values.Count != 2)
            {
                offences.Add(new Offence(Collapse(call.Text), "the file operation line takes exactly {Operation} and {Failure}"));
                continue;
            }

            if (!FileOperationValue().IsMatch(values[0]) && !operationNames.Contains(values[0]))
            {
                offences.Add(new Offence(Collapse(call.Text), $"{{Operation}} is not a LibraryFileOperation ({values[0]})"));
            }

            if (!FailureShapeValue().IsMatch(values[1]))
            {
                offences.Add(new Offence(Collapse(call.Text), $"{{Failure}} is not FailureShape.Describe output ({values[1]})"));
            }
        }

        return offences;
    }

    // A helper takes the exception itself before its template and renders it by shape. The values after the template
    // are forwarded to the log as they are, so an exception among them would be logged whole.
    private static IEnumerable<string> JudgeForwarded(IReadOnlyList<string> args, HashSet<string> exceptionNames)
    {
        var template = -1;
        for (var i = 0; i < args.Count && template < 0; i++)
        {
            if (IsStringLiteral(args[i]))
            {
                template = i;
            }
        }

        for (var i = template < 0 ? args.Count : template + 1; i < args.Count; i++)
        {
            if (IsExceptionObject(args[i], exceptionNames))
            {
                yield return $"argument {i} forwards an exception object as a value ({args[i]})";
            }
        }
    }

    // An exception under any disguise a cast gives it: "(object)ex", "((object?)ex!)", "ex as object".
    private static bool IsExceptionObject(string arg, HashSet<string> exceptionNames)
    {
        var core = Unwrap(arg);
        return exceptionNames.Contains(core) || ExceptionMember().IsMatch(core);
    }

    // The expression with the casts, enclosing parentheses, null-forgiving operators and "as" conversions around it
    // taken away, until nothing more comes off.
    internal static string Unwrap(string expression)
    {
        var core = StripComments(expression).Trim();
        while (true)
        {
            var before = core;
            core = core.TrimEnd('!').TrimEnd();
            if (EnclosingParentheses(core))
            {
                core = core[1..^1].Trim();
            }
            else if (LeadingCast().Match(core) is { Success: true } cast)
            {
                core = core[cast.Length..].Trim();
            }
            else if (TrailingAs().Match(core) is { Success: true } conversion)
            {
                core = conversion.Groups["operand"].Value.Trim();
            }

            if (core == before)
            {
                return core;
            }
        }
    }

    // True when the opening parenthesis at the start closes at the very end, so the pair wraps the whole expression.
    private static bool EnclosingParentheses(string expression)
    {
        if (expression.Length < 2 || expression[0] != '(' || expression[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        var i = 0;
        while (i < expression.Length)
        {
            var literalEnd = SkipLiteralOrComment(expression, i);
            if (literalEnd > i)
            {
                i = literalEnd;
                continue;
            }

            if (expression[i] == '(')
            {
                depth++;
            }
            else if (expression[i] == ')' && --depth == 0)
            {
                return i == expression.Length - 1;
            }

            i++;
        }

        return false;
    }

    private static IEnumerable<string> Judge(LogCall call, HashSet<string> exceptionNames)
    {
        var args = call.Arguments;
        int template;
        if (call.Method != "Log")
        {
            template = 0;
        }
        else if (args.Count > 1 && IsStringLiteral(args[1]))
        {
            template = 1; // Log(level, template, ...)
        }
        else if (args.Count > 2 && IsStringLiteral(args[2]))
        {
            template = 2; // Log(level, eventId, template, ...); an exception there is caught below
        }
        else if (args.Count == 5)
        {
            // The raw ILogger.Log(level, eventId, state, exception, formatter) form, as a logger
            // wrapper forwards: it must hand on no exception object at all.
            if (args[3] != "null")
            {
                yield return "passes an exception object to the raw Log overload";
            }

            template = -1;
        }
        else if (args.Count == 3 && ForwardedTemplates.Contains(args[1]) && ForwardedValues.Contains(args[2]))
        {
            // Log(level, template, values) inside a logging helper, forwarding what its callers passed.
            // The template is theirs, so only what is forwarded can be judged, and the callers are
            // checked as helper calls.
            template = -1;
        }
        else
        {
            template = 1;
        }

        if (template >= 0 && (template >= args.Count || !IsStringLiteral(args[template])))
        {
            yield return "the message template is not a string literal (an exception object first?)";
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (IsExceptionObject(arg, exceptionNames))
            {
                yield return $"argument {i} is an exception object ({arg})";
            }
        }

        foreach (var reason in JudgeText(args, exceptionNames))
        {
            yield return reason;
        }
    }

    // The checks that apply to anything headed for the log, a helper's arguments included.
    private static IEnumerable<string> JudgeText(IReadOnlyList<string> args, HashSet<string> exceptionNames)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (ExceptionText().IsMatch(StripLiterals(arg)))
            {
                yield return $"argument {i} reads exception text ({arg})";
            }

            if (ToStringCall().IsMatch(StripLiterals(arg)))
            {
                yield return $"argument {i} renders an object with ToString() ({arg})";
            }

            // Data holds whatever the thrower attached, which can be anything the failure touched.
            foreach (Match data in ExceptionData().Matches(StripLiterals(arg)))
            {
                var target = data.Groups["target"].Value;
                if (exceptionNames.Contains(target) || ExceptionMember().IsMatch(target))
                {
                    yield return $"argument {i} reads an exception's Data ({arg})";
                    break;
                }
            }

            foreach (var hole in InterpolationHoles(arg))
            {
                var root = LeadingIdentifier(hole);
                if (root is not null && exceptionNames.Contains(root) && !IsSafeExceptionMember(hole.TrimStart()[root.Length..]))
                {
                    yield return $"argument {i} interpolates an exception ({{{hole}}})";
                }
            }
        }
    }

    // Its type and its HResult say what failed without quoting anything the failure carried.
    private static bool IsSafeExceptionMember(string rest)
    {
        var member = rest.TrimStart().TrimStart('?', '!');
        return member.StartsWith(".GetType()", StringComparison.Ordinal) || member.StartsWith(".HResult", StringComparison.Ordinal);
    }

    private static bool IsStringLiteral(string arg) =>
        arg.StartsWith('"') || arg.StartsWith("@\"", StringComparison.Ordinal) ||
        arg.StartsWith("$\"", StringComparison.Ordinal) || arg.StartsWith("$@\"", StringComparison.Ordinal) ||
        arg.StartsWith("@$\"", StringComparison.Ordinal);

    private static string? LeadingIdentifier(string expression)
    {
        var trimmed = expression.TrimStart();
        var length = 0;
        while (length < trimmed.Length && (char.IsLetterOrDigit(trimmed[length]) || trimmed[length] == '_'))
        {
            length++;
        }

        return length == 0 ? null : trimmed[..length];
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

    // Reads from the opening parenthesis at `open` to its match, splitting top-level commas.
    internal static bool TryReadArguments(string source, int open, out List<string> arguments, out int close)
    {
        arguments = [];
        close = -1;
        var depth = 0;
        var current = new StringBuilder();
        var i = open + 1;
        while (i < source.Length)
        {
            var literalEnd = SkipLiteralOrComment(source, i);
            if (literalEnd > i)
            {
                current.Append(source, i, literalEnd - i);
                i = literalEnd;
                continue;
            }

            var c = source[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (c != ')')
                    {
                        return false;
                    }

                    AddArgument(arguments, current);
                    close = i;
                    return true;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddArgument(arguments, current);
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        return false;
    }

    private static void AddArgument(List<string> arguments, StringBuilder current)
    {
        var text = StripComments(current.ToString()).Trim();
        if (text.Length > 0)
        {
            arguments.Add(text);
        }
    }

    // The end of a literal or comment starting at i, or i itself when none starts there.
    private static int SkipLiteralOrComment(string s, int i)
    {
        if (Starts(s, i, "//"))
        {
            var end = s.IndexOf('\n', i);
            return end < 0 ? s.Length : end;
        }

        if (Starts(s, i, "/*"))
        {
            var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 2;
        }

        if (Starts(s, i, "\"\"\""))
        {
            var end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 3;
        }

        if (Starts(s, i, "$@\"") || Starts(s, i, "@$\""))
        {
            return SkipInterpolated(s, i + 3, verbatim: true);
        }

        if (Starts(s, i, "$\""))
        {
            return SkipInterpolated(s, i + 2, verbatim: false);
        }

        if (Starts(s, i, "@\""))
        {
            return SkipString(s, i + 2, verbatim: true);
        }

        if (s[i] == '"')
        {
            return SkipString(s, i + 1, verbatim: false);
        }

        if (s[i] == '\'')
        {
            var j = i + 1;
            while (j < s.Length && s[j] != '\'')
            {
                j += s[j] == '\\' ? 2 : 1;
            }

            return Math.Min(j + 1, s.Length);
        }

        return i;
    }

    private static int SkipString(string s, int j, bool verbatim)
    {
        while (j < s.Length)
        {
            if (verbatim && s[j] == '"' && j + 1 < s.Length && s[j + 1] == '"')
            {
                j += 2;
                continue;
            }

            if (!verbatim && s[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (s[j] == '"')
            {
                return j + 1;
            }

            j++;
        }

        return s.Length;
    }

    private static int SkipInterpolated(string s, int j, bool verbatim)
    {
        while (j < s.Length)
        {
            if (Starts(s, j, "{{") || Starts(s, j, "}}"))
            {
                j += 2;
                continue;
            }

            if (s[j] == '{')
            {
                j = SkipHole(s, j + 1);
                continue;
            }

            if (verbatim && Starts(s, j, "\"\""))
            {
                j += 2;
                continue;
            }

            if (!verbatim && s[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (s[j] == '"')
            {
                return j + 1;
            }

            j++;
        }

        return s.Length;
    }

    // From just inside a hole's '{' to just past its '}', skipping nested literals and brackets.
    private static int SkipHole(string s, int j)
    {
        var depth = 0;
        while (j < s.Length)
        {
            var literalEnd = SkipLiteralOrComment(s, j);
            if (literalEnd > j)
            {
                j = literalEnd;
                continue;
            }

            if (s[j] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (s[j] is ')' or ']')
            {
                depth--;
            }
            else if (s[j] == '}')
            {
                if (depth == 0)
                {
                    return j + 1;
                }

                depth--;
            }

            j++;
        }

        return s.Length;
    }

    // The expressions inside an interpolated string argument's holes.
    internal static IEnumerable<string> InterpolationHoles(string arg)
    {
        var start = arg.StartsWith("$\"", StringComparison.Ordinal) ? 2
            : arg.StartsWith("$@\"", StringComparison.Ordinal) || arg.StartsWith("@$\"", StringComparison.Ordinal) ? 3
            : -1;
        if (start < 0)
        {
            yield break;
        }

        var j = start;
        while (j < arg.Length)
        {
            if (Starts(arg, j, "{{") || Starts(arg, j, "}}"))
            {
                j += 2;
                continue;
            }

            if (arg[j] == '{')
            {
                var end = SkipHole(arg, j + 1);
                yield return arg[(j + 1)..Math.Max(j + 1, end - 1)];
                j = end;
                continue;
            }

            j++;
        }
    }

    // Literal text removed, so a template that merely mentions ".Message" is not mistaken for code.
    private static string StripLiterals(string arg)
    {
        var builder = new StringBuilder(arg.Length);
        var i = 0;
        while (i < arg.Length)
        {
            var end = SkipLiteralOrComment(arg, i);
            if (end > i)
            {
                // Interpolation holes are code, so they stay; everything else in the literal goes.
                foreach (var hole in InterpolationHoles(arg[i..end]))
                {
                    builder.Append(' ').Append(hole).Append(' ');
                }

                builder.Append("\"\"");
                i = end;
                continue;
            }

            builder.Append(arg[i]);
            i++;
        }

        return builder.ToString();
    }

    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (Starts(text, i, "//") || Starts(text, i, "/*"))
            {
                i = SkipLiteralOrComment(text, i);
                continue;
            }

            var end = SkipLiteralOrComment(text, i);
            if (end > i)
            {
                builder.Append(text, i, end - i);
                i = end;
                continue;
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool Starts(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
}
