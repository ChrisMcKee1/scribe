using Scribe.Core.Models;

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
