using Scribe.Core.Libraries;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Settings;

/// <summary>Writes the word-pack part of Settings' save draft, normalized to what a Save submits.</summary>
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

        var snapshot = workspace.Draft;
        draft.Number(snapshot.Libraries.Count(library => !library.PendingDelete));
        foreach (var library in snapshot.Libraries.Where(library => !library.PendingDelete))
        {
            var content = library.Content;
            draft.Part("pack")
                .Text(content.Id)
                .Flag(content.BuiltIn)
                .Text(content.Name)
                .Text(content.Category)
                .Text(content.Description)
                .Text(content.BasedOn)
                .Flag(snapshot.LocalState.EnabledIds.Contains(content.Id))
                .Flag(workspace.ShowsAiPermission(content.Id))
                .Number(content.Rows.Count);
            foreach (var row in content.Rows)
            {
                draft.Part("term")
                    .Text(row.Values.Spoken)
                    .Text(row.Values.Written)
                    .Flag(row.Values.WholeWord)
                    .Flag(row.Values.Enabled)
                    .Number((long)row.Origin);
            }
        }

        return draft;
    }
}
