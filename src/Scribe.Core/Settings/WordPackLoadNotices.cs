using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

public sealed record WordPackLoadNoticeCandidate(
    string Key,
    string? LibraryId,
    WordPackNotice Notice,
    bool RestorePreviousAvailable = true);

public static class WordPackLoadNotices
{
    public static WordPackLoadNoticeCandidate? Select(LibraryCatalog catalog, LibraryComposition? composition = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var library in catalog.Libraries)
        {
            if (library.State == LibraryFileState.AwaitingRelease)
            {
                return Candidate("awaiting", library, WordPackNotices.FromLoad(WordPackLoadState.AwaitingRelease, library.Content.Name));
            }

            if (library.Content.BuiltIn && library.State is LibraryFileState.Unreadable or LibraryFileState.Newer)
            {
                return Candidate(
                    "paused",
                    library,
                    WordPackNotices.FromLoad(WordPackLoadState.PausedBuiltIn, library.Content.Name),
                    library.PreviousEditsAvailable);
            }

            if (library.State == LibraryFileState.PartlyReadable || library.ReadErrorCount > 0)
            {
                return Candidate("partly", library, WordPackNotices.FromLoad(WordPackLoadState.PartlyReadable, library.Content.Name));
            }
        }

        if (catalog.LocalState.AiPermissionsLost)
        {
            return new WordPackLoadNoticeCandidate("aiPermissionsLost", null, WordPackNotices.FromLoad(WordPackLoadState.AiPermissionsLost));
        }

        foreach (var kept in catalog.KeptVersions)
        {
            var name = catalog.Find(kept.LibraryId)?.Content.Name ?? kept.LibraryId;
            return new WordPackLoadNoticeCandidate("kept:" + kept.LibraryId + ":" + kept.Kind, kept.LibraryId, WordPackNotices.FromKeptVersion(kept.Kind, name));
        }

        if (composition?.AnyLegacyMarkerActive == true)
        {
            return new WordPackLoadNoticeCandidate("legacyMarker", null, WordPackNotices.FromLoad(WordPackLoadState.LegacyMarker));
        }

        if (catalog.LocalState.AiUpgradeNotice.Count > 0)
        {
            return new WordPackLoadNoticeCandidate("upgrade", catalog.LocalState.AiUpgradeNotice.FirstOrDefault(), WordPackNotices.FromLoad(WordPackLoadState.Upgrade));
        }

        return null;
    }

    private static WordPackLoadNoticeCandidate Candidate(
        string prefix,
        CatalogLibrary library,
        WordPackNotice notice,
        bool restorePreviousAvailable = true) =>
        new(prefix + ":" + library.Content.Id + ":" + library.State, library.Content.Id, notice, restorePreviousAvailable);
}
