using Scribe.Core.Settings;
using Row = Scribe.Core.Settings.DictionaryEntryBuilder.Row;

namespace Scribe.Core.Tests;

public sealed class DictionaryWordEditorTests
{
    [Fact]
    public void Adding_several_ways_preserves_each_phrase_and_uses_the_normal_defaults()
    {
        var existing = new Row[] { new(17, "old words", "Old", false, false) };
        string[] forms = ["co pilot", "copilot", "co, pile it", "two  spaces", "line\nbreak"];

        var result = DictionaryWordEditor.Build(existing, null, " Copilot\r\ntext ", forms);

        Assert.True(result.Succeeded);
        Assert.Null(result.EditedRow);
        Assert.Equal(forms, result.AddedRows.Select(row => row.Pattern));
        Assert.All(result.AddedRows, row =>
        {
            Assert.Equal(0, row.Id);
            Assert.Equal(" Copilot\r\ntext ", row.Replacement);
            Assert.True(row.WholeWord);
            Assert.True(row.Enabled);
        });
        Assert.Equal(new Row(17, "old words", "Old", false, false), existing[0]);
    }

    [Fact]
    public void Editing_is_scoped_to_the_selected_row_even_when_others_write_the_same_text()
    {
        Row[] existing =
        [
            new(7, "sequel", "SQL", true, true),
            new(12, "unrelated", "Elsewhere", true, true),
            new(19, "ess queue ell", "SQL", false, false),
        ];
        var before = existing.ToArray();

        var result = DictionaryWordEditor.Build(existing, 2, "T-SQL", ["ess queue ell", "another way"]);

        Assert.True(result.Succeeded);
        Assert.Equal(new Row(19, "ess queue ell", "T-SQL", false, false), result.EditedRow);
        Assert.Equal(new Row(0, "another way", "T-SQL", true, true), Assert.Single(result.AddedRows));
        Assert.Equal(before, existing);
    }

    [Fact]
    public void Unsaved_rows_with_zero_ids_are_distinguished_by_the_selected_position()
    {
        Row[] existing = [new(0, "first", "Same", false, true), new(0, "second", "Same", true, false)];

        var result = DictionaryWordEditor.Build(existing, 1, "Different", ["second"]);

        Assert.True(result.Succeeded);
        Assert.Equal(new Row(0, "second", "Different", true, false), result.EditedRow);
        Assert.Equal("Same", existing[0].Replacement);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Blank_new_ways_are_ignored(string blank)
    {
        var result = DictionaryWordEditor.Build([], null, "Written", [blank, "one way", blank]);

        Assert.True(result.Succeeded);
        Assert.Equal("one way", Assert.Single(result.AddedRows).Pattern);
    }

    [Fact]
    public void An_empty_written_form_creates_separate_removal_rules()
    {
        Row[] existing = [new(8, "um", "", true, true)];
        var result = DictionaryWordEditor.Build(existing, null, "", ["uh", "erm"]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AddedRows.Count);
        Assert.All(result.AddedRows, row => Assert.Equal("", row.Replacement));
        Assert.Null(result.EditedRow);
    }

    [Fact]
    public void An_untouched_edit_is_identical_including_spaces_line_endings_and_flags()
    {
        var original = new Row(8, " two  words,\nnext ", "Template\nbody\r\nend", false, false);

        var result = DictionaryWordEditor.Build([original], 0, original.Replacement!, [original.Pattern!]);

        Assert.True(result.Succeeded);
        Assert.Equal(original, result.EditedRow);
        Assert.Empty(result.AddedRows);
    }

    [Theory]
    [InlineData("FIRST")]
    [InlineData(" first ")]
    [InlineData("\tfirst\n")]
    public void Existing_spoken_forms_conflict_even_when_the_other_rule_is_off(string form)
    {
        Row[] existing = [new(8, "first", "Elsewhere", false, false)];

        var result = DictionaryWordEditor.Build(existing, null, "Written", ["unique", form]);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ErrorFormIndex);
        Assert.Contains("already in your dictionary", result.Error);
        Assert.Empty(result.AddedRows);
        Assert.Null(result.EditedRow);
    }

    [Fact]
    public void Duplicate_ways_in_the_same_dialog_are_rejected_without_partial_additions()
    {
        var result = DictionaryWordEditor.Build([], null, "Written", ["one way", " ONE WAY "]);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ErrorFormIndex);
        Assert.Empty(result.AddedRows);
    }

    [Fact]
    public void An_unchanged_legacy_duplicate_can_be_opened_without_tightening_save_validation()
    {
        Row[] existing = [new(1, "legacy", "Same", true, true), new(2, "LEGACY", "Same", false, false)];

        var result = DictionaryWordEditor.Build(existing, 0, "Same", ["legacy"]);

        Assert.True(result.Succeeded);
        Assert.Equal(existing[0], result.EditedRow);
        Assert.False(DictionaryWordEditor.Build(existing, 0, "Changed", ["legacy"]).Succeeded);
        Assert.False(DictionaryWordEditor.Build(existing, 0, "Same", ["legacy", "LEGACY"]).Succeeded);
    }

    [Fact]
    public void Unrelated_legacy_duplicates_do_not_block_adding_a_different_word()
    {
        Row[] existing = [new(1, "legacy", "One", true, true), new(2, "LEGACY", "Two", false, false)];

        Assert.True(DictionaryWordEditor.Build(existing, null, "Written", ["different"]).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Clearing_the_selected_spoken_form_respects_the_existing_settings_validation(string blank)
    {
        var result = DictionaryWordEditor.Build([new(7, "old", "Written", true, true)], 0, "Written", [blank, "new"]);

        Assert.False(result.Succeeded);
        Assert.Equal(SettingsDraftValidator.DictionarySpokenEmptyMessage, result.Error);
        Assert.Empty(result.AddedRows);
    }

    [Fact]
    public void Adding_nothing_reports_an_error()
    {
        Assert.False(DictionaryWordEditor.Build([], null, "Written", []).Succeeded);
        Assert.False(DictionaryWordEditor.Build([], null, "Written", ["", " \t"]).Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void An_invalid_edit_scope_is_never_treated_as_an_add(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DictionaryWordEditor.Build([new(7, "old", "Written", true, true)], index, "Written", ["new"]));
    }

    [Theory]
    [InlineData("first\nsecond", "first\r\nsecond", "first\r\nsecond", "first\nsecond")]
    [InlineData("first\nsecond", "first\r\nsecond", "changed\r\nsecond", "changed\r\nsecond")]
    [InlineData("Case", "Case", "case", "case")]
    [InlineData(" two  words ", " two  words ", " two  words ", " two  words ")]
    public void Display_normalization_does_not_rewrite_untouched_text(
        string original, string displayed, string current, string expected)
    {
        Assert.Equal(expected, DictionaryWordEditor.PreserveUnchangedText(original, displayed, current));
    }
}
