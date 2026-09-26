using Scribe.Core.Vocabulary;

namespace Scribe.Core.Settings;

/// <summary>Writes the word-pack part of Settings' save draft as the user's edit revision.</summary>
public static class WordPackDraftSignature
{
    public static DraftSnapshot Write(DraftSnapshot draft, LibraryWorkspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Part("word-pack-workspace");
        if (workspace is null)
        {
            return draft.Text(null);
        }

        return draft.Number(workspace.EditRevision);
    }
}
