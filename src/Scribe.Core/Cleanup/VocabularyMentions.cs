using Scribe.Core.Models;

namespace Scribe.Core.Cleanup;

/// <summary>
/// How much of the vocabulary each AI cleanup request carries. The vocabulary is every enabled word of the dictionary
/// and of the word packs AI cleanup may use, up to the budget of the instructions in use
/// (<see cref="CleanupPrompt.GlossaryTermBudget"/>).
/// </summary>
public enum CleanupVocabularyMode
{
    /// <summary>All of it, whether or not the dictation mentions a word: what every release before 0.5.2 sent.</summary>
    All = 0,

    /// <summary>Only the words the dictation appears to mention (<see cref="VocabularyMentions"/>), within the same budget.</summary>
    Mentioned = 1,

    /// <summary>None of it. The dictionary still corrects the text on this PC.</summary>
    None = 2,
}

/// <summary>
/// Which vocabulary entries a dictation appears to mention, so a cleanup request can carry those rather than the whole
/// vocabulary. An entry is mentioned when its written or spoken form turns up in the dictation, allowing for what speech
/// recognition does to names: split or joined words ("o llama", "text embedding 3 large"), sound-alike spellings ("Alama"
/// for Ollama, "Quen" for Qwen) and single-letter slips on longer words. A word of more than one part counts only when
/// every part of it does, so "GPT" alone does not bring in every GPT model.
/// </summary>
/// <remarks>
/// Built to err toward including: an entry included by mistake costs a few tokens, one left out can cost the spelling it
/// was there for. Pure and deterministic, with no allocation per entry beyond the entry's own parts.
/// </remarks>
internal static class VocabularyMentions
{
    // Common words an entry's form can contain that say nothing about whether the dictation mentions it.
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "into", "onto", "that", "this", "your", "you", "our", "are", "was", "not",
        "but", "all", "any", "can", "has", "have", "one", "two", "new", "use", "get", "set", "run", "out", "off", "via",
        "per", "max", "min", "its", "his", "her", "who", "why", "how", "what", "when", "where", "which", "will", "just",
        "large", "small", "model", "models", "version", "preview", "latest", "base", "chat", "instruct",
    };

    /// <summary>The entries of <paramref name="entries"/> that <paramref name="dictation"/> appears to mention, in their order.</summary>
    public static IReadOnlyList<DictionaryEntry> Select(IReadOnlyList<DictionaryEntry> entries, string? dictation)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(dictation) || entries.Count == 0)
        {
            return [];
        }

        var text = new DictationWords(Parts(dictation));
        if (text.IsEmpty)
        {
            return [];
        }

        var selected = new List<DictionaryEntry>();
        foreach (var entry in entries)
        {
            if (entry is { Enabled: true } && (Mentions(text, entry.Replacement) || Mentions(text, entry.Pattern)))
            {
                selected.Add(entry);
            }
        }

        return selected;
    }

    // True when the dictation mentions this written or spoken form.
    internal static bool Mentions(DictationWords text, string? form)
    {
        if (string.IsNullOrWhiteSpace(form))
        {
            return false;
        }

        var parts = Parts(form);
        if (parts.Count == 0)
        {
            return false;
        }

        // The whole form, joined, against runs of the dictation's words joined the same way: "ollama" in "o llama",
        // "gpt56terra" in "gpt 56 tera".
        var joined = string.Concat(parts);
        if (joined.Length >= 3 && text.HasJoinedRun(joined))
        {
            return true;
        }

        // The same by sound, digits kept as they are, for names of letters and numbers: "Phi-4-mini" in "fi4 mini",
        // "Qwen3-32B" in "quen 332b".
        if (parts.Count >= 2 && SoundOf(parts) is { Length: >= 3 } sound && text.HasSoundRun(sound))
        {
            return true;
        }

        // Every word of the form (numbers and single letters aside, which say nothing on their own) found in the dictation.
        var words = 0;
        foreach (var part in parts)
        {
            if (part.Length < 3 || !char.IsLetter(part[0]) || CommonWords.Contains(part))
            {
                continue;
            }

            words++;
            if (!text.HasWord(part))
            {
                return false;
            }
        }

        return words > 0;
    }

    // The parts' sound keys joined, with numbers as they are.
    private static string SoundOf(List<string> parts)
    {
        var sound = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            sound.Append(char.IsDigit(part[0]) ? part : SoundKey(part));
        }

        return sound.ToString();
    }

    /// <summary>
    /// The lowercase parts of a text: runs of letters and runs of digits, so "Qwen3-14B" is qwen, 3, 14, b and
    /// "GPT-5.6-Terra" is gpt, 5, 6, terra. Anything else separates parts.
    /// </summary>
    internal static List<string> Parts(string text)
    {
        var parts = new List<string>();
        var start = -1;
        var digits = false;
        for (var i = 0; i <= text.Length; i++)
        {
            var ch = i < text.Length ? text[i] : ' ';
            var isLetter = char.IsLetter(ch);
            var isDigit = char.IsDigit(ch);
            if (start >= 0 && (!(isLetter || isDigit) || isDigit != digits))
            {
                parts.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }

            if (start < 0 && (isLetter || isDigit))
            {
                start = i;
                digits = isDigit;
            }
        }

        return parts;
    }

    /// <summary>
    /// A rough sound key for a word of letters: vowels after the first letter dropped (a leading vowel kept as "a"),
    /// letters that sound alike merged, and repeats collapsed, so "ollama" and "alama" share "alm" and "qwen" and "quen"
    /// share "kn". Empty for anything but letters.
    /// </summary>
    internal static string SoundKey(string word)
    {
        if (word.Length == 0 || !char.IsLetter(word[0]))
        {
            return string.Empty;
        }

        var key = new System.Text.StringBuilder(word.Length);
        for (var i = 0; i < word.Length; i++)
        {
            var ch = word[i];
            var next = i + 1 < word.Length ? word[i + 1] : '\0';
            char? mapped = ch switch
            {
                'a' or 'e' or 'i' or 'o' or 'u' or 'y' => i == 0 ? 'a' : null,
                'h' or 'w' => null,
                'p' when next == 'h' => 'f',
                'c' when next is 'e' or 'i' or 'y' => 's',
                'c' or 'k' or 'q' => 'k',
                'g' when next is 'e' or 'i' or 'y' => 'j',
                'j' => 'j',
                'x' => 'k',
                'z' => 's',
                'v' => 'f',
                'b' => 'b',
                'd' or 't' => 't',
                _ when char.IsLetter(ch) => ch,
                _ => null,
            };

            if (mapped is { } m && (key.Length == 0 || key[^1] != m))
            {
                key.Append(m);
            }
        }

        return key.ToString();
    }

    // Levenshtein distance, stopping early once it passes max.
    internal static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }

            if (best > max)
            {
                return max + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // How far apart two words or joined runs may be and still be the same name: exact under 5 letters, one slip from 5,
    // two from 10.
    private static int Allowance(int length) => length >= 10 ? 2 : length >= 5 ? 1 : 0;

    /// <summary>The dictation's parts, indexed for the lookups <see cref="Mentions"/> makes.</summary>
    internal sealed class DictationWords
    {
        // Runs of up to this many of the dictation's parts are joined: enough for "text embedding 3 large".
        private const int MaxRun = 6;
        private readonly HashSet<string> _words = new(StringComparer.Ordinal);
        private readonly HashSet<string> _soundKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<int, List<string>> _wordsByLength = [];
        private readonly HashSet<string> _runs = new(StringComparer.Ordinal);
        private readonly Dictionary<(char First, int Length), List<string>> _runsByShape = [];
        private readonly HashSet<string> _soundRuns = new(StringComparer.Ordinal);

        public DictationWords(List<string> parts)
        {
            IsEmpty = parts.Count == 0;
            var sounds = new string[parts.Count];
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                sounds[i] = char.IsDigit(part[0]) ? part : SoundKey(part);
                if (!_words.Add(part) || !char.IsLetter(part[0]))
                {
                    continue;
                }

                if (part.Length >= 4)
                {
                    _soundKeys.Add(sounds[i]);
                }

                if (!_wordsByLength.TryGetValue(part.Length, out var sameLength))
                {
                    sameLength = [];
                    _wordsByLength[part.Length] = sameLength;
                }

                sameLength.Add(part);
            }

            for (var start = 0; start < parts.Count; start++)
            {
                var joined = new System.Text.StringBuilder();
                var sound = new System.Text.StringBuilder();
                for (var end = start; end < parts.Count && end - start < MaxRun; end++)
                {
                    joined.Append(parts[end]);
                    sound.Append(sounds[end]);
                    var run = joined.ToString();
                    if (_runs.Add(run))
                    {
                        var shape = (run[0], run.Length);
                        if (!_runsByShape.TryGetValue(shape, out var sameShape))
                        {
                            sameShape = [];
                            _runsByShape[shape] = sameShape;
                        }

                        sameShape.Add(run);
                    }

                    if (end > start)
                    {
                        _soundRuns.Add(sound.ToString());
                    }
                }
            }
        }

        public bool IsEmpty { get; }

        // A word of an entry: the same word, the same sound for words of 4 letters or more, or a slip within the allowance.
        public bool HasWord(string word)
        {
            if (_words.Contains(word))
            {
                return true;
            }

            if (word.Length >= 4 && _soundKeys.Contains(SoundKey(word)))
            {
                return true;
            }

            var allowance = Allowance(word.Length);
            for (var length = word.Length - allowance; allowance > 0 && length <= word.Length + allowance; length++)
            {
                if (_wordsByLength.TryGetValue(length, out var candidates))
                {
                    foreach (var candidate in candidates)
                    {
                        if (Distance(word, candidate, allowance) <= allowance)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // A form joined into one string, against runs of the dictation's parts joined the same way: exactly, or within
        // the allowance for a run that starts with the same letter.
        public bool HasJoinedRun(string joined)
        {
            if (_runs.Contains(joined))
            {
                return true;
            }

            var allowance = Allowance(joined.Length);
            for (var length = joined.Length - allowance; allowance > 0 && length <= joined.Length + allowance; length++)
            {
                if (_runsByShape.TryGetValue((joined[0], length), out var candidates))
                {
                    foreach (var candidate in candidates)
                    {
                        if (Distance(joined, candidate, allowance) <= allowance)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // A form's parts by sound, against runs of two or more of the dictation's parts by sound.
        public bool HasSoundRun(string sound) => _soundRuns.Contains(sound);
    }
}
