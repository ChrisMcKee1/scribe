using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// Try dictation's "unsaved" comparison (<c>SettingsChangeTracker.Compare(_committedSettings, TryDictationDraft())</c>)
/// reads the page as Save would store it. Pinned by source because the window has no tests of its own: a draft that reads
/// a control differently from Save (the model picker's display text, a hidden sign-in method's fields, no subscription)
/// kept Try dictation's unsaved warning up right after a Save (AI review, rounds 4 and 5).
/// </summary>
public sealed class TryDictationDraftCoverageTests
{
    // Values the Save stores that the draft deliberately leaves as saved, with the reason.
    private static readonly Dictionary<string, string> NotInTheDraft = new(StringComparer.Ordinal)
    {
        ["LaunchOnLogin"] = "Start with Windows applies from its own switch the moment it is flipped, so it is never unsaved.",
    };

    [Fact]
    public void The_draft_assigns_every_value_the_save_stores_with_the_save_s_own_expression()
    {
        var root = RepositoryRoot();
        var window = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "src", "Scribe.App", "Settings"), "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));
        var tryDictation = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.TryDictation.cs"));
        Assert.Contains("using Scribe.Core.Transcription;", tryDictation, StringComparison.Ordinal);
        var save = Body(window, "private async Task<bool> TrySaveAsync()");
        var draft = Body(tryDictation, "private AppSettings TryDictationDraft()");
        var captured = Body(window, "private AppSettings CaptureDraftSettings()");

        var stored = Assignments(save, "_settings");
        var copy = save.LastIndexOf("CopySettings(preflight.Settings, _settings);", StringComparison.Ordinal);
        var store = save.IndexOf("await _wordPackSaveProtocol.SaveAsync(", StringComparison.Ordinal);
        Assert.True(copy >= 0 && copy < store, "Save must store the captured preflight, not later control values.");
        Assert.DoesNotMatch(@"_settings\.(?!LaunchOnLogin\b)\w+\s*=(?![=>])", save[copy..store]);
        Assert.Contains("var settings = CaptureDraftSettings();",
            Body(window, "private async Task<SavePreflightInput?> PrepareSavePreflightAsync()"), StringComparison.Ordinal);
        var copier = Body(window, "private static void CopySettings(");
        Assert.Contains("var copy = source.Clone();", copier, StringComparison.Ordinal);
        Assert.Contains("property.CanRead && property.CanWrite && property.Name != nameof(AppSettings.LaunchOnLogin)", copier, StringComparison.Ordinal);
        Assert.Contains("property.SetValue(target, property.GetValue(copy));", copier, StringComparison.Ordinal);
        foreach (var (property, expression) in Assignments(captured, "draft"))
        {
            stored[property] = expression;
        }

        var drafted = Assignments(draft, "draft");
        Assert.True(stored.Count >= 40, $"Only {stored.Count} Save assignments were found.");

        foreach (var (property, expression) in stored)
        {
            if (NotInTheDraft.ContainsKey(property))
            {
                continue;
            }

            Assert.True(drafted.TryGetValue(property, out var draftExpression), $"Try dictation's draft does not set {property}, which Save stores.");
            var expected = Normalize(Expand(expression, captured, "draft"), "draft");
            var actual = Normalize(Expand(draftExpression!, draft, "draft"), "draft");
            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                $"Try dictation's draft reads {property} as `{draftExpression}`, but Save stores `{expression}`.");
        }

        Assert.All(NotInTheDraft.Keys, property => Assert.Contains(property, stored.Keys));
    }

    // Each property the body assigns on the named settings object, with the expression it assigns.
    private static Dictionary<string, string> Assignments(string body, string target)
    {
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(body, $@"(?<![\w.]){Regex.Escape(target)}\.(?<property>\w+)\s*=(?![=>])\s*(?<value>[^;]+);"))
        {
            assignments[match.Groups["property"].Value] = match.Groups["value"].Value;
        }

        return assignments;
    }

    // The expression with every local of its body written out, one level at a time, so a value Save computes into a local
    // and the draft writes inline (or through its own local of the same definition) compare alike.
    private static string Expand(string expression, string body, string target)
    {
        var expanded = expression;
        for (var depth = 0; depth < 4; depth++)
        {
            var next = Regex.Replace(expanded, @"(?<![\w.])(?<name>[a-z]\w*)\b(?!\s*\()", match =>
            {
                if (match.Value == target)
                {
                    return match.Value;
                }

                var local = Regex.Match(body, $@"\bvar\s+{match.Groups["name"].Value}\s*=\s*(?<value>[^;]+);");
                return local.Success ? "(" + local.Groups["value"].Value + ")" : match.Value;
            });
            if (next == expanded)
            {
                break;
            }

            expanded = next;
        }

        return expanded;
    }

    // Whitespace and redundant parentheses around a written-out local don't count, and each side's own settings object
    // stands for the settings being built.
    private static string Normalize(string expression, string target)
    {
        var squashed = Regex.Replace(expression, @"\s+", string.Empty);
        squashed = squashed.Replace("Scribe.Core.Transcription.TranscriptionModelCatalog.", "TranscriptionModelCatalog.", StringComparison.Ordinal);
        squashed = Regex.Replace(squashed, $@"(?<![\w.]){Regex.Escape(target)}\.", "SETTINGS.");
        return squashed.Replace("(", string.Empty, StringComparison.Ordinal).Replace(")", string.Empty, StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} has no end.");
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
