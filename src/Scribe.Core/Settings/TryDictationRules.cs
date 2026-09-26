using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

public static class TryDictationSample
{
    public const string Default = "Not sure what to say? Try: \"This is a test of Scribe on my PC.\"";

    public static IReadOnlyList<string> For(IEnumerable<DictionaryEntry> dictionaryEntries)
    {
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        var samples = new List<string> { Default };
        var spoken = dictionaryEntries
            .Where(entry => entry.Enabled)
            .Select(entry => entry.Pattern.Trim())
            .FirstOrDefault(pattern => pattern.Length > 0);
        if (spoken is not null)
        {
            samples.Add($"Or try: \"Please book a meeting about {spoken}.\"");
        }

        return samples;
    }
}

public static class InjectionMethodLabel
{
    public static string Describe(InjectionMethod method, bool addedSpace) =>
        WithSpace(method switch
        {
            InjectionMethod.UnicodeType => "Typed as keystrokes",
            InjectionMethod.ClipboardPaste => "Pasted",
            _ => "Inserted directly",
        }, addedSpace);

    public static string Describe(string? methodCode, bool addedSpace)
    {
        var label = (methodCode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "unicode" or "unicode-type" or "type" or "keystrokes" => "Typed as keystrokes",
            "clipboard" or "clipboard-paste" or "paste" => "Pasted",
            "win32-edit" or "direct" => "Inserted directly",
            _ => "Inserted directly",
        };
        return WithSpace(label, addedSpace);
    }

    private static string WithSpace(string label, bool addedSpace) =>
        addedSpace ? $"{label}, then a space" : label;
}


public static class TryDictationTiming
{
    public static TimeSpan ProcessingDuration(
        TimeSpan trimmingSilence,
        TimeSpan speechRecognition,
        TimeSpan aiCleanup,
        TimeSpan dictionaryAndSnippets,
        TimeSpan typing) =>
        trimmingSilence + speechRecognition + aiCleanup + dictionaryAndSnippets + typing;

    public static string SpeedLabel(double realTimeFactor)
    {
        if (realTimeFactor <= 0 || double.IsNaN(realTimeFactor) || double.IsInfinity(realTimeFactor))
        {
            return string.Empty;
        }

        var faster = Math.Max(1, (int)Math.Round(1 / realTimeFactor, MidpointRounding.AwayFromZero));
        return $"{faster:N0} times faster than real time";
    }
}

public static class TryDictationChangeList
{
    public const string DictionaryOrWordPackSource = "your dictionary or a word pack";
    public const string AiCleanupChanged = "AI cleanup rewrote the text.";

    public static IReadOnlyList<string> Describe(
        IEnumerable<TextReplacement> replacements,
        bool aiCleanupChanged)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var lines = replacements.Select(Describe).ToList();
        if (aiCleanupChanged)
        {
            lines.Add(AiCleanupChanged);
        }

        return lines;
    }

    public static string Describe(TextReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var source = replacement.Kind == TextReplacementKind.Snippet
            ? "snippet"
            : DictionaryOrWordPackSource;
        return $"\"{replacement.Pattern}\" became \"{replacement.Replacement}\" ({source})";
    }
}
