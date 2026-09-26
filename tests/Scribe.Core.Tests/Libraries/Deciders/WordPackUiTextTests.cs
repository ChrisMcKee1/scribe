using Scribe.Core.Libraries;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackUiTextTests
{
    [Fact]
    public void List_meta_uses_source_words_unsaved_and_matches()
    {
        Assert.Equal("Built-in, 94 words", WordPackUiText.ListMeta(true, 94, false));
        Assert.Equal("Imported, 1 word, Unsaved, 1 match", WordPackUiText.ListMeta(false, 1, true, 1));
    }

    [Fact]
    public void Header_meta_includes_category_and_unsaved_state()
    {
        Assert.Equal("Built-in, Data and AI, 197 words, Unsaved", WordPackUiText.HeaderMeta(true, "Data and AI", 197, true));
    }

    [Fact]
    public void Ai_help_names_the_recipient()
    {
        Assert.Equal(
            "Sends this word pack's words as vocabulary with every AI cleanup request, whether or not you say them.",
            WordPackUiText.AiHelp(true, foundryLocal: false));
        Assert.Equal(
            "Not sent as vocabulary. Words you dictate still reach the AI service in the text.",
            WordPackUiText.AiHelp(false, foundryLocal: false));
        Assert.Equal("AI cleanup runs on this PC, so nothing leaves it.", WordPackUiText.AiHelp(false, foundryLocal: true));
    }

    [Theory]
    [InlineData(TermMarker.NotUsed, "Not used")]
    [InlineData(TermMarker.Update, "Update")]
    [InlineData(TermMarker.OffHere, "Off here")]
    [InlineData(TermMarker.Check, "Check")]
    [InlineData(TermMarker.Removes, "Removes")]
    [InlineData(TermMarker.Changed, "Changed")]
    public void Marker_label_is_the_one_word_status(TermMarker marker, string expected)
    {
        Assert.Equal(expected, WordPackUiText.MarkerLabel(marker));
    }
}
