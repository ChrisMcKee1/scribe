using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>Pure copy and summary decisions for the Word packs editor surface.</summary>
public static class WordPackUiText
{
    public const string EmptySelectedPack = "No words yet. Add a word or phrase and how it should be written.";

    public static string WordCount(int count) => $"{count:N0} {(count == 1 ? "word" : "words")}";

    public static string MatchCount(int count) => $"{count:N0} {(count == 1 ? "match" : "matches")}";

    public static string ListMeta(bool builtIn, int words, bool unsaved, int? matches = null)
    {
        var text = $"{Source(builtIn)}, {WordCount(words)}";
        if (unsaved)
        {
            text += ", Unsaved";
        }

        if (matches is > 0 and var count)
        {
            text += $", {MatchCount(count)}";
        }

        return text;
    }

    public static string HeaderMeta(bool builtIn, string category, int words, bool unsaved)
    {
        var text = string.IsNullOrWhiteSpace(category)
            ? $"{Source(builtIn)}, {WordCount(words)}"
            : $"{Source(builtIn)}, {category}, {WordCount(words)}";
        return unsaved ? $"{text}, Unsaved" : text;
    }

    public static string Source(bool builtIn) => builtIn ? "Built-in" : "Imported";

    public static string TermCount(int visible, int total) =>
        visible == total ? WordCount(total) : $"{WordCount(visible)} of {WordCount(total)}";

    public static string AiHelp(bool checkedState, bool foundryLocal) =>
        foundryLocal
            ? "AI cleanup runs on this PC, so nothing leaves it."
            : checkedState
                ? "Sends this word pack's words as vocabulary with every AI cleanup request, whether or not you say them."
                : "Not sent as vocabulary. Words you dictate still reach the AI service in the text.";

    public static string MarkerLabel(TermMarker marker) => marker switch
    {
        TermMarker.NotUsed => "Not used",
        TermMarker.Update => "Update",
        TermMarker.OffHere => "Off here",
        TermMarker.Check => "Check",
        TermMarker.Removes => "Removes",
        TermMarker.Changed => "Changed",
        _ => string.Empty,
    };

    public static string GlossaryLine(GlossaryInclusion inclusion) => inclusion switch
    {
        GlossaryInclusion.Included => "Sent to AI cleanup as vocabulary.",
        GlossaryInclusion.NotPermitted => "Not sent to AI cleanup: this word pack isn't used in AI cleanup.",
        GlossaryInclusion.OverBudget => "Not sent to AI cleanup: the vocabulary is full.",
        _ => "Not sent to AI cleanup: this word is not included in vocabulary.",
    };

    public static string RowErrorReason(LibraryCsvRowErrorKind kind) => kind switch
    {
        LibraryCsvRowErrorKind.MissingFields => "missing a value",
        LibraryCsvRowErrorKind.EmptySpoken => "Scribe hears is empty",
        LibraryCsvRowErrorKind.InvalidWholeWord => "whole words value is not true or false",
        LibraryCsvRowErrorKind.InvalidEnabled => "on value is not true or false",
        LibraryCsvRowErrorKind.UnclosedQuote => "a quoted value is not closed",
        LibraryCsvRowErrorKind.FieldTooLong => "a value is too long",
        _ => "row format is not supported",
    };
}
