using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class DictionaryDraftDiffTests
{
    [Fact] public void New_rows_are_reported() => Assert.Contains("new", DictionaryDraftDiff.ChangedSpokenForms([], [Entry("new", "New")]), StringComparer.OrdinalIgnoreCase);
    [Fact] public void Edited_rows_are_reported() => Assert.Contains("old", DictionaryDraftDiff.ChangedSpokenForms([Entry("old", "Old")], [Entry("old", "New")]), StringComparer.OrdinalIgnoreCase);
    [Fact] public void Deleted_rows_are_reported() => Assert.Contains("gone", DictionaryDraftDiff.ChangedSpokenForms([Entry("gone", "Gone")], []), StringComparer.OrdinalIgnoreCase);
    [Fact] public void Unchanged_rows_are_not_reported() => Assert.Empty(DictionaryDraftDiff.ChangedSpokenForms([Entry(" same ", "Same")], [Entry("same", "Same")]));
    [Fact] public void Case_and_spaces_do_not_count_as_changes() => Assert.Empty(DictionaryDraftDiff.ChangedSpokenForms([Entry(" Cloud ", "Copilot")], [Entry("cloud", "Copilot")]));

    private static DictionaryEntry Entry(string pattern, string replacement) => new(1, pattern, replacement);
}
