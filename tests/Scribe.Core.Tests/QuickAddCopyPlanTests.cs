using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddCopyPlanTests
{
    [Fact]
    public void A_pending_valid_correction_is_saved_before_copying()
    {
        var request = new QuickDictionaryAdd.QuickAddRequest("cloud pilot", "Copilot", false, true, "ask cloud pilot");
        var copy = Copy(request, dirtySinceSave: true);

        Assert.True(copy.CanCopy);
        Assert.True(copy.SaveFirst);
        Assert.Equal("Save and co_py", copy.ButtonText);
        Assert.Equal("Save and copy dictation", copy.Name);
        Assert.Equal("Save this correction, then copy the corrected dictation and keep this window open.", copy.HelpText);
    }

    [Fact]
    public void A_saved_dictation_or_an_emptied_draft_needs_no_second_save()
    {
        foreach (var dirty in new[] { false, true })
        {
            var copy = Copy(new QuickDictionaryAdd.QuickAddRequest("", "", false, true, "ask Copilot"), dirty);

            Assert.True(copy.CanCopy);
            Assert.False(copy.SaveFirst);
            Assert.Equal("Co_py dictation", copy.ButtonText);
            Assert.Equal("Copy dictation", copy.Name);
        }
    }

    [Theory]
    [InlineData("", "Copilot", false, true)]
    [InlineData("cloud pilot", "", false, true)]
    [InlineData("cloud pilot", "cloud pilot", false, true)]
    [InlineData("cloud\npilot", "Copilot", false, true)]
    [InlineData("cloud pilot", "Copilot", false, false)]
    public void An_invalid_pending_correction_cannot_copy_a_stale_dictation(string heard, string writes, bool remove, bool referencesAvailable)
    {
        var copy = Copy(new QuickDictionaryAdd.QuickAddRequest(
            heard, writes, remove, true, "ask cloud pilot", ReferencesAvailable: referencesAvailable), dirtySinceSave: true);

        Assert.False(copy.CanCopy);
        Assert.True(copy.SaveFirst);
        Assert.Contains("Finish a valid correction", copy.HelpText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void A_missing_or_deleted_source_cannot_be_copied_even_with_a_valid_correction(string? transcript)
    {
        var request = new QuickDictionaryAdd.QuickAddRequest("cloud pilot", "Copilot", false, true, transcript);
        var vocabulary = QuickAddVocabulary.Compose([], [], [], []);
        var correction = QuickDictionaryAdd.Build(request, vocabulary);
        var copy = QuickDictionaryAdd.BuildCopy(request, correction, dirtySinceSave: true);

        Assert.True(correction.CanSave);
        Assert.False(copy.CanCopy);
        Assert.Contains("You can still save a word without one.", copy.HelpText);
    }

    [Fact]
    public void Removing_words_is_a_pending_save_too()
    {
        var copy = Copy(new QuickDictionaryAdd.QuickAddRequest("filler", "", true, true, "remove filler here"), dirtySinceSave: true);

        Assert.True(copy.CanCopy);
        Assert.True(copy.SaveFirst);
    }

    [Fact]
    public void A_conflict_with_the_open_Settings_draft_cannot_be_copied()
    {
        var request = new QuickDictionaryAdd.QuickAddRequest("cloud pilot", "Copilot", false, true, "ask cloud pilot");
        var vocabulary = QuickAddVocabulary.Compose(
            Array.Empty<DictionaryEntry>(), ["cloud pilot"], Array.Empty<DictionaryLibrary>(), []);
        var correction = QuickDictionaryAdd.Build(request, vocabulary);

        Assert.Equal(QuickDictionaryAdd.PlanKind.BlockedBySettings, correction.Kind);
        Assert.False(QuickDictionaryAdd.BuildCopy(request, correction, dirtySinceSave: true).CanCopy);
    }

    private static QuickDictionaryAdd.CopyPlan Copy(QuickDictionaryAdd.QuickAddRequest request, bool dirtySinceSave) =>
        QuickDictionaryAdd.BuildCopy(request, QuickDictionaryAdd.Build(request, QuickAddVocabulary.Compose([], [], [], [])), dirtySinceSave);
}
