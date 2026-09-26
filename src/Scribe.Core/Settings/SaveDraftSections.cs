using Scribe.Core.Vocabulary;

namespace Scribe.Core.Settings;

/// <summary>Normalizes asynchronously loaded Settings sections for Save's draft comparison.</summary>
public sealed class SaveDraftSections
{
    private const string NotLoadedToken = "not-loaded";

    private readonly Section _dictionary;
    private readonly Section _snippets;
    private readonly Section _wordPacks;

    public SaveDraftSections(Section dictionary, Section snippets, Section wordPacks)
    {
        _dictionary = dictionary;
        _snippets = snippets;
        _wordPacks = wordPacks;
    }

    public Capture CaptureNow() => new(_dictionary.Loaded, _snippets.Loaded, _wordPacks.Loaded);

    public DraftSnapshot Write(DraftSnapshot draft, Capture? capture)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Write(draft, "dictionary", _dictionary, capture?.DictionaryLoaded);
        Write(draft, "snippets", _snippets, capture?.SnippetsLoaded);
        return Write(draft, "word-packs", _wordPacks, capture?.WordPacksLoaded);
    }

    private static DraftSnapshot Write(DraftSnapshot draft, string name, Section section, bool? capturedLoaded)
    {
        draft.Part(name);
        var unchangedFirstLoad = capturedLoaded == false && section.Loaded && !section.Differs;
        return !section.Loaded || unchangedFirstLoad
            ? draft.Text(NotLoadedToken)
            : draft.Text(section.ContentSignature);
    }

    public sealed record Section(bool Loaded, bool Differs, string ContentSignature);

    public sealed record Capture(bool DictionaryLoaded, bool SnippetsLoaded, bool WordPacksLoaded);
}
