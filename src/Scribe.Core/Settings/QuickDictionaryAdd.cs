using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

public static class QuickDictionaryAdd
{
    private static readonly char[] EdgePunctuation = ['.', ',', '!', '?', ';', ':', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>', '…', '\u2014', '\u2013', '\u00ab', '\u00bb', '\u201c', '\u201d', '\u2018', '\u2019'];

    public readonly record struct Token(string Text, int Start, int Length);

    public readonly record struct WordRange(int First, int Last)
    {
        public static WordRange None => new(-1, -1);
        public bool IsEmpty => First < 0;
        public int Count => IsEmpty ? 0 : Last - First + 1;
        public bool Contains(int index) => !IsEmpty && index >= First && index <= Last;
    }

    public enum PlanKind
    {
        Invalid,
        Create,
        Update,
        NoChange,
        Empty,
        LineBreak,
        SameText,
        BlockedBySettings,
        ProducedByYourWord,
        ProducedByWordPack,
        PendingExisting,
        UpdateTurnOn,
        UpdateReplacement,
        UpdateToRemoval,
        UpdateFromRemoval,
        UpdateWholeWord,
        PendingWordPack,
        SameAsWordPack,
        OverridesWordPack,
        Pending,
        CreateRemoval,
        ReferencesUnavailable,
        SaveFailed,
        Saved,
        CopySucceeded,
        CopyFailed,
    }

    public enum PlanSeverity
    {
        None,
        Info,
        Warning,
        Error,
        Success,
    }

    public enum PlanAction
    {
        None,
        ShowInSettings,
        FixInstead,
        AddItInSettings,
        CopyFixedDictation,
        TryAgain,
    }

    public sealed record QuickAddRequest(
        string? Heard,
        string? Writes,
        bool Remove,
        bool WholeWord,
        string? PickedTranscript = null,
        bool DictionaryOn = true,
        bool ReferencesAvailable = true);

    public readonly record struct Plan(
        PlanKind Kind,
        DictionaryEntry? Entry,
        string Message,
        PlanSeverity Severity = PlanSeverity.Info,
        PlanAction Action = PlanAction.None,
        string? ActionTarget = null,
        bool IsRemoval = false)
    {
        public bool CanSave => Kind is PlanKind.Create or PlanKind.Update or PlanKind.UpdateTurnOn or PlanKind.UpdateReplacement or PlanKind.UpdateToRemoval or PlanKind.UpdateFromRemoval or PlanKind.UpdateWholeWord or PlanKind.OverridesWordPack or PlanKind.CreateRemoval;
    }

    public readonly record struct CopyPlan(bool CanCopy, bool SaveFirst, string ButtonText, string Name, string HelpText);

    public static CopyPlan BuildCopy(QuickAddRequest request, Plan correction, bool dirtySinceSave)
    {
        var pending = dirtySinceSave
            && (!string.IsNullOrWhiteSpace(request.Heard)
                || !string.IsNullOrWhiteSpace(request.Writes)
                || request.Remove);
        var hasTranscript = !string.IsNullOrWhiteSpace(request.PickedTranscript);
        var canCopy = hasTranscript && (!pending || correction.CanSave);
        var help = !hasTranscript
            ? "Choose a recent dictation to copy. You can still save a word without one."
            : pending && !correction.CanSave
                ? "Finish a valid correction before copying. Copying saves it first and keeps this window open."
                : pending
                    ? "Save this correction, then copy the corrected dictation and keep this window open."
                    : "Copy the current dictation and keep this window open.";
        return new CopyPlan(
            canCopy,
            pending,
            pending ? "Save and co_py" : "Co_py dictation",
            pending ? "Save and copy dictation" : "Copy dictation",
            help);
    }

    public static WordRange Toggle(WordRange current, int index)
    {
        if (index < 0) return current;
        if (current.IsEmpty) return new WordRange(index, index);
        var first = current.First;
        var last = current.Last;
        if (index == first - 1) return new WordRange(index, last);
        if (index == last + 1) return new WordRange(first, index);
        if (index >= first && index <= last)
        {
            if (first == last) return WordRange.None;
            if (index == first) return new WordRange(first + 1, last);
            if (index == last) return new WordRange(first, last - 1);
            return new WordRange(index, index);
        }

        return new WordRange(index, index);
    }

    public static IReadOnlyList<Token> Tokenize(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return [];
        var tokens = new List<Token>();
        var i = 0;
        while (i < transcript.Length)
        {
            if (char.IsWhiteSpace(transcript[i])) { i++; continue; }
            var start = i;
            while (i < transcript.Length && !char.IsWhiteSpace(transcript[i])) i++;
            tokens.Add(new Token(transcript[start..i], start, i - start));
        }

        return tokens;
    }

    public static string Select(string? transcript, IReadOnlyList<Token> tokens, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrEmpty(transcript) || tokens.Count == 0) return string.Empty;
        if (first > last) (first, last) = (last, first);
        first = Math.Clamp(first, 0, tokens.Count - 1);
        last = Math.Clamp(last, 0, tokens.Count - 1);
        var start = tokens[first].Start;
        var end = tokens[last].Start + tokens[last].Length;
        if (start < 0 || end > transcript.Length || end <= start) return string.Empty;
        var raw = Collapse(transcript[start..end]);
        var trimmed = raw.Trim(EdgePunctuation).Trim();
        return trimmed.Length == 0 ? raw : trimmed;
    }

    public static Plan Build(string? pattern, string? replacement, bool wholeWord, IReadOnlyList<DictionaryEntry> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var spoken = (pattern ?? string.Empty).Trim();
        if (spoken.Length == 0)
        {
            return new Plan(PlanKind.Invalid, null, "Pick a word above, or type what Scribe wrote.");
        }

        if (spoken.Contains('\n') || spoken.Contains('\r'))
        {
            return new Plan(PlanKind.Invalid, null, "Pick words from a single line. A rule can't stretch across a line break.");
        }

        var written = (replacement ?? string.Empty).Trim();
        if (string.Equals(spoken, written, StringComparison.Ordinal))
        {
            return new Plan(PlanKind.Invalid, null, "That is already what Scribe writes, so nothing would change.");
        }

        var producer = existing.FirstOrDefault(e =>
            e.Enabled
            && string.Equals(e.Replacement.Trim(), spoken, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.Pattern.Trim(), spoken, StringComparison.OrdinalIgnoreCase));
        if (producer is not null)
        {
            return new Plan(
                PlanKind.Invalid,
                null,
                $"\"{producer.Pattern.Trim()}\" is already turned into \"{spoken}\" by another rule, and rules " +
                $"only run once, so this would never apply. Change that rule's replacement instead.");
        }

        var match = existing.FirstOrDefault(e => string.Equals(e.Pattern.Trim(), spoken, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            if (string.Equals(match.Replacement.Trim(), written, StringComparison.Ordinal)
                && match.WholeWord == wholeWord
                && match.Enabled)
            {
                return new Plan(PlanKind.NoChange, match, $"\"{spoken}\" already becomes \"{LegacyDescribe(written)}\".");
            }

            var updated = new DictionaryEntry(match.Id, spoken, written, wholeWord, Enabled: true);
            return new Plan(
                PlanKind.Update,
                updated,
                $"Replaces the existing rule: \"{spoken}\" becomes \"{LegacyDescribe(written)}\" instead of \"{LegacyDescribe(match.Replacement.Trim())}\".");
        }

        var created = new DictionaryEntry(0, spoken, written, wholeWord, Enabled: true);
        return new Plan(
            PlanKind.Create,
            created,
            written.Length == 0
                ? $"Scribe will leave \"{spoken}\" out of what you dictate."
                : $"Scribe will write \"{spoken}\" as \"{written}\".");
    }

    public static Plan Build(QuickAddRequest request, QuickAddVocabulary vocabulary)
    {
        var heard = (request.Heard ?? string.Empty).Trim();
        var written = (request.Writes ?? string.Empty).Trim();
        if (heard.Length == 0)
        {
            return new Plan(PlanKind.Empty, null, string.Empty, PlanSeverity.None);
        }

        if (!request.ReferencesAvailable)
        {
            return new Plan(PlanKind.ReferencesUnavailable, null, "Couldn't check your dictionary. Try again.", PlanSeverity.Error, PlanAction.TryAgain);
        }

        if (heard.Contains('\n') || heard.Contains('\r'))
        {
            return new Plan(PlanKind.LineBreak, null, "Select words from one line. A dictionary word can't span a line break.", PlanSeverity.Warning);
        }

        if (request.Remove)
        {
            written = string.Empty;
        }
        else if (string.Equals(heard, written, StringComparison.Ordinal))
        {
            return new Plan(PlanKind.SameText, null, $"Scribe already writes \"{heard}\" this way. Type what it should write instead.", PlanSeverity.Info);
        }

        if (vocabulary.PendingSpokenForms.Contains(heard))
        {
            return new Plan(PlanKind.BlockedBySettings, null, $"You're changing \"{heard}\" in Settings. Save or undo that change there first.", PlanSeverity.Warning, PlanAction.ShowInSettings, heard);
        }

        var producer = vocabulary.FindProducer(heard);
        if (producer is { } foundProducer)
        {
            var fromPack = foundProducer.PackName is not null;
            var producerMessage = fromPack
                ? $"Scribe writes \"{heard}\" because the \"{foundProducer.PackName}\" word pack changes \"{foundProducer.Entry.Pattern.Trim()}\" to it. To change what it writes, fix \"{foundProducer.Entry.Pattern.Trim()}\" instead."
                : $"Scribe writes \"{heard}\" because your dictionary changes \"{foundProducer.Entry.Pattern.Trim()}\" to it. To change what it writes, fix \"{foundProducer.Entry.Pattern.Trim()}\" instead.";
            return new Plan(fromPack ? PlanKind.ProducedByWordPack : PlanKind.ProducedByYourWord, null, producerMessage, PlanSeverity.Warning, PlanAction.FixInstead, foundProducer.Entry.Pattern.Trim());
        }

        var personal = vocabulary.FindPersonal(heard);
        if (personal is not null)
        {
            return PlanForPersonal(request, personal, heard, written);
        }

        var pack = vocabulary.FindPack(heard);
        if (pack is not null)
        {
            return PlanForPack(request, pack, heard, written);
        }

        if (!request.Remove && written.Length == 0)
        {
            return new Plan(PlanKind.Pending, null, $"Type what Scribe should write instead of \"{heard}\".", PlanSeverity.Info);
        }

        var created = new DictionaryEntry(0, heard, written, request.WholeWord, Enabled: true);
        var kind = request.Remove ? PlanKind.CreateRemoval : PlanKind.Create;
        var message = request.Remove
            ? $"Scribe will remove \"{heard}\" from everything you dictate{InsideLongerWords(request)}."
            : $"Scribe will write \"{written}\" when it hears \"{heard}\"{InsideLongerWords(request)}.";
        return new Plan(kind, created, message, request.Remove ? PlanSeverity.Warning : PlanSeverity.Info, IsRemoval: request.Remove);
    }

    public static Plan Saved(QuickAddRequest request)
    {
        var heard = (request.Heard ?? string.Empty).Trim();
        var written = (request.Writes ?? string.Empty).Trim();
        var message = request.Remove
            ? $"Saved. Scribe will remove \"{heard}\" from everything you dictate."
            : $"Saved. Scribe will write \"{written}\" when it hears \"{heard}\".";
        return new Plan(PlanKind.Saved, null, message, PlanSeverity.Success, PlanAction.CopyFixedDictation, IsRemoval: request.Remove);
    }

    public static Plan SaveFailed() => new(PlanKind.SaveFailed, null, "Couldn't save this word. Try again, or add it in Settings.", PlanSeverity.Error, PlanAction.AddItInSettings);
    public static Plan CopySucceeded() => new(PlanKind.CopySucceeded, null, "Copied. Press Ctrl+V to paste it.", PlanSeverity.Success);
    public static Plan CopyFailed() => new(PlanKind.CopyFailed, null, "Couldn't copy. Another app may be using the clipboard. Try again.", PlanSeverity.Error);

    private static string LegacyDescribe(string written) => written.Length == 0 ? "nothing" : written;

    private static Plan PlanForPersonal(QuickAddRequest request, DictionaryEntry match, string heard, string written)
    {
        var old = match.Replacement.Trim();
        if (!request.Remove && written.Length == 0)
        {
            var message = old.Length == 0
                ? $"\"{heard}\" is already in your dictionary: Scribe removes it."
                : $"\"{heard}\" is already in your dictionary: Scribe writes \"{old}\". Type something else to change it.";
            return new Plan(PlanKind.PendingExisting, null, message, PlanSeverity.Info);
        }

        if (string.Equals(old, written, StringComparison.Ordinal) && match.WholeWord == request.WholeWord && match.Enabled)
        {
            var message = written.Length == 0
                ? $"\"{heard}\" is already in your dictionary: Scribe removes it."
                : $"\"{heard}\" is already in your dictionary: Scribe writes \"{written}\".";
            return new Plan(PlanKind.NoChange, match, AddOlderDictationSentence(message, request, heard), PlanSeverity.Info);
        }

        var updated = new DictionaryEntry(match.Id, heard, written, request.WholeWord, Enabled: true);
        if (!match.Enabled && string.Equals(old, written, StringComparison.Ordinal) && match.WholeWord == request.WholeWord)
        {
            return new Plan(PlanKind.UpdateTurnOn, updated, $"You already have \"{heard}\" in your dictionary, but it's turned off. Saving turns it back on.", PlanSeverity.Warning);
        }

        if (old.Length > 0 && written.Length == 0)
        {
            return new Plan(PlanKind.UpdateToRemoval, updated, $"You already have \"{heard}\" in your dictionary. Saving makes Scribe remove it instead of writing \"{old}\".{ExtraUpdateSentences(match, request, written)}", PlanSeverity.Warning, IsRemoval: true);
        }

        if (old.Length == 0 && written.Length > 0)
        {
            return new Plan(PlanKind.UpdateFromRemoval, updated, $"You already have \"{heard}\" in your dictionary, set to remove it. Saving makes Scribe write \"{written}\" instead.{ExtraUpdateSentences(match, request, written)}", PlanSeverity.Warning);
        }

        if (string.Equals(old, written, StringComparison.Ordinal) && match.WholeWord != request.WholeWord)
        {
            var target = request.WholeWord ? "match whole words only" : "also match inside longer words";
            return new Plan(PlanKind.UpdateWholeWord, updated, $"You already have \"{heard}\" in your dictionary. Saving changes it to {target}.", PlanSeverity.Warning);
        }

        return new Plan(PlanKind.UpdateReplacement, updated, $"You already have \"{heard}\" in your dictionary. Saving changes what Scribe writes from \"{old}\" to \"{written}\".{ExtraUpdateSentences(match, request, written)}", PlanSeverity.Warning);
    }

    private static Plan PlanForPack(QuickAddRequest request, PackTerm pack, string heard, string written)
    {
        var old = pack.Entry.Replacement.Trim();
        if (!request.Remove && written.Length == 0)
        {
            return new Plan(PlanKind.PendingWordPack, null, $"The \"{pack.PackName}\" word pack writes \"{heard}\" as \"{old}\". Type something else to use your own spelling.", PlanSeverity.Info);
        }

        if (!request.Remove && string.Equals(old, written, StringComparison.Ordinal) && pack.Entry.WholeWord == request.WholeWord)
        {
            var message = $"The \"{pack.PackName}\" word pack already writes \"{heard}\" as \"{written}\", so there's nothing to add.";
            return new Plan(PlanKind.SameAsWordPack, null, AddOlderDictationSentence(message, request, heard), PlanSeverity.Info);
        }

        var entry = new DictionaryEntry(0, heard, written, request.WholeWord, Enabled: true);
        var result = request.Remove
            ? $"The \"{pack.PackName}\" word pack writes \"{heard}\" as \"{old}\". Your word will win: Scribe will remove it."
            : $"The \"{pack.PackName}\" word pack writes \"{heard}\" as \"{old}\". Your word will win: Scribe will write \"{written}\".";
        return new Plan(PlanKind.OverridesWordPack, entry, result, PlanSeverity.Info, IsRemoval: request.Remove);
    }

    private static string ExtraUpdateSentences(DictionaryEntry match, QuickAddRequest request, string written)
    {
        var parts = new List<string>();
        if (!match.Enabled) parts.Add(" It also turns it back on.");
        if (match.WholeWord != request.WholeWord)
        {
            parts.Add(request.WholeWord ? " It also changes it to match whole words only." : " It also changes it to also match inside longer words.");
        }

        return string.Concat(parts);
    }

    private static string AddOlderDictationSentence(string message, QuickAddRequest request, string heard) =>
        request.DictionaryOn && ContainsPickedText(request.PickedTranscript, heard)
            ? message + " Dictations made before it was added keep the old words."
            : message;

    private static bool ContainsPickedText(string? pickedTranscript, string heard) =>
        !string.IsNullOrWhiteSpace(pickedTranscript) && pickedTranscript.Contains(heard, StringComparison.OrdinalIgnoreCase);

    private static string InsideLongerWords(QuickAddRequest request) => request.WholeWord ? string.Empty : ", even inside longer words";

    /// <summary>
    /// <see cref="Build(string, string, bool, IReadOnlyList{DictionaryEntry})"/> against the dictionary composed with
    /// the committed libraries only (review finding R12): <paramref name="committedLibraries"/> is a
    /// <see cref="LibraryVocabulary"/>, which only a committed catalog makes, so a library correction the Libraries page
    /// has not saved can never make quick add say a rule "already becomes" something. The dictionary wins over the
    /// libraries exactly as dictation merges them.
    /// </summary>
    public static Plan Build(
        string? pattern,
        string? replacement,
        bool wholeWord,
        IReadOnlyList<DictionaryEntry> dictionaryRows,
        LibraryVocabulary committedLibraries)
    {
        ArgumentNullException.ThrowIfNull(dictionaryRows);
        ArgumentNullException.ThrowIfNull(committedLibraries);
        return Build(pattern, replacement, wholeWord, DictionaryLibraryComposer.Merge(dictionaryRows, committedLibraries.Entries));
    }

    /// <summary>
    /// The unsaved libraries of <paramref name="draft"/> that would apply a rule for <paramref name="spoken"/> other than
    /// the one the committed libraries apply, for "Also in an unsaved library": each is on in the draft, has unsaved
    /// changes, and has an enabled row with that spoken form whose written form or whole-word value differs from the
    /// committed rule, or the committed libraries have none. Ids, in precedence order.
    /// </summary>
    public static IReadOnlyList<string> UnsavedLibraryMatches(LibraryDraft draft, LibraryVocabulary committed, string spoken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(committed);
        var key = LibraryTermKey.From(spoken);
        if (key.IsEmpty)
        {
            return [];
        }

        var applied = committed.Entries.FirstOrDefault(entry => LibraryTermKey.From(entry.Pattern) == key);
        var matches = new List<string>();
        foreach (var library in draft.Libraries)
        {
            if (!library.Unsaved || library.PendingDelete || !draft.LocalState.EnabledIds.Contains(library.Content.Id))
            {
                continue;
            }

            var differs = library.Content.Rows.Any(row =>
                row.Values.Enabled
                && LibraryTermKey.From(row.Values.Spoken) == key
                && (applied is null
                    || !string.Equals(row.Values.Written, applied.Replacement, StringComparison.Ordinal)
                    || row.Values.WholeWord != applied.WholeWord));
            if (differs)
            {
                matches.Add(library.Content.Id);
            }
        }

        return matches;
    }

    /// <summary>
    /// Applies one just-saved rule to a transcript the user has already seen, so the copy kept for
    /// clipboard recovery reads the way they just told Scribe it should read.
    ///
    /// Only this rule is applied, never the whole dictionary. The transcript is already
    /// post-dictionary text, so re-running every rule could rewrite words the user never touched.
    ///
    /// The work is delegated to <see cref="TextPostProcessor.ApplyRule"/> on purpose. An earlier
    /// version reimplemented the matcher here and diverged from the live pipeline in two ways that
    /// both produced visibly wrong text: it normalized whitespace after replacing instead of before,
    /// and it lacked the guard that stops "york" to "New York" firing again on text that already
    /// reads "New York".
    /// </summary>
    /// <returns>The rewritten transcript, or the original unchanged when the rule cannot apply.</returns>
    public static string Apply(string? transcript, DictionaryEntry? entry)
        => TextPostProcessor.ApplyRule(transcript, entry);

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n')
            {
                pendingSpace = false;
                builder.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}

public sealed record PackTerm(string PackName, DictionaryEntry Entry);

public sealed record QuickAddVocabulary(
    IReadOnlyList<DictionaryEntry> Personal,
    IReadOnlySet<string> PendingSpokenForms,
    IReadOnlyList<PackTerm> Packs)
{
    public static QuickAddVocabulary Compose(
        IEnumerable<DictionaryEntry> personal,
        IEnumerable<string> pendingSpokenForms,
        IEnumerable<DictionaryLibrary> libraries,
        IEnumerable<string> enabledIds)
    {
        ArgumentNullException.ThrowIfNull(personal);
        ArgumentNullException.ThrowIfNull(pendingSpokenForms);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(enabledIds);
        var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        var personalList = personal.Where(e => !string.IsNullOrWhiteSpace(e.Pattern)).ToList();
        var seen = new HashSet<string>(personalList.Where(e => e.Enabled).Select(e => e.Pattern.Trim()), StringComparer.OrdinalIgnoreCase);
        var packs = new List<PackTerm>();
        foreach (var library in LibraryPrecedence.Order(libraries).Where(l => enabled.Contains(l.Id)))
        {
            foreach (var entry in library.EnabledEntries)
            {
                var key = entry.Pattern.Trim();
                if (key.Length == 0 || !seen.Add(key)) continue;
                packs.Add(new PackTerm(library.Name, entry));
            }
        }

        return new QuickAddVocabulary(personalList, new HashSet<string>(pendingSpokenForms.Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase), packs);
    }

    public DictionaryEntry? FindPersonal(string spoken) => Personal.FirstOrDefault(e => string.Equals(e.Pattern.Trim(), spoken.Trim(), StringComparison.OrdinalIgnoreCase));
    public PackTerm? FindPack(string spoken) => Packs.FirstOrDefault(e => string.Equals(e.Entry.Pattern.Trim(), spoken.Trim(), StringComparison.OrdinalIgnoreCase));
    public PackTerm? FindPackProducer(string written) => Packs.FirstOrDefault(e => string.Equals(e.Entry.Replacement.Trim(), written.Trim(), StringComparison.OrdinalIgnoreCase) && !string.Equals(e.Entry.Pattern.Trim(), written.Trim(), StringComparison.OrdinalIgnoreCase));
    public (DictionaryEntry Entry, string? PackName)? FindProducer(string written)
    {
        var personal = Personal.FirstOrDefault(e => e.Enabled && string.Equals(e.Replacement.Trim(), written.Trim(), StringComparison.OrdinalIgnoreCase) && !string.Equals(e.Pattern.Trim(), written.Trim(), StringComparison.OrdinalIgnoreCase));
        if (personal is not null) return (personal, null);
        var pack = FindPackProducer(written);
        return pack is null ? null : (pack.Entry, pack.PackName);
    }
}
