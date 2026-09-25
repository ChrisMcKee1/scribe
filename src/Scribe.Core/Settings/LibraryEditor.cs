using System.Globalization;
using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>The row commands the Libraries grid offers for one term, as flags.</summary>
[Flags]
public enum TermCommands
{
    None = 0,

    /// <summary>Turn off term: the row stays and contributes no rule.</summary>
    TurnOff = 1,

    /// <summary>Turn on term.</summary>
    TurnOn = 2,

    /// <summary>
    /// Delete term: only a row the user owns (a custom library's row, or a built-in row with no shipped counterpart:
    /// added, or no longer shipped).
    /// </summary>
    Delete = 4,

    /// <summary>
    /// Restore built-in values: an authored built-in row with shipped values (edited, pinned, or an addition a later
    /// version ships) back to what this version ships.
    /// </summary>
    RestoreBuiltIn = 8,

    /// <summary>Show other sources: for a row turned off here, which may still be applied by another library.</summary>
    ShowOtherSources = 16,

    /// <summary>Copy the row's values.</summary>
    Copy = 32,

    /// <summary>Copy to my dictionary.</summary>
    CopyToDictionary = 64,
}

/// <summary>Which metadata field of a custom library a validation issue is about.</summary>
public enum LibraryMetadataField
{
    /// <summary>The issue is not about metadata.</summary>
    None,

    Name,

    Category,

    Description,

    /// <summary>The <c># based-on:</c> id of a duplicate, which the user never types but which is part of the header.</summary>
    BasedOn,
}

/// <summary>
/// How the library editor commits what the user types, which commands a term row offers, and the validation messages
/// shown beside a field.
/// </summary>
/// <remarks>
/// Only the editor normalizes values (plan 3.6, review finding R8): a Spoken value is committed trimmed with every inner
/// run of white space collapsed to one space (<see cref="LibraryTermKey.Normalize"/>), so everything the editor writes is
/// in the form the key and the matcher agree on (review finding A10); a Written value is only trimmed, its inner line
/// breaks and spacing kept, never flattened. Storage keeps whatever it is given. Messages quote the user's own words and
/// are for the screen only; nothing here is ever logged.
/// </remarks>
public static class LibraryEditor
{
    /// <summary>
    /// The message beside a name, category or description that holds a double quote, which older versions of Scribe read
    /// as a quoted field and which would hide the library's rows there (review finding A11).
    /// </summary>
    public const string MetadataDoubleQuoteMessage =
        "Names can't contain a double quote (\"), which older versions of Scribe misread.";

    /// <summary>A typed Spoken value as the editor commits it: trimmed, inner white space collapsed to one space, case kept.</summary>
    public static string CommitSpoken(string? typed) => LibraryTermKey.Normalize(typed);

    /// <summary>A typed Written value as the editor commits it: trimmed only, inner line breaks and spacing kept.</summary>
    public static string CommitWritten(string? typed) => (typed ?? string.Empty).Trim();

    /// <summary><paramref name="typed"/> with both text values committed; the whole-word and enabled flags as given.</summary>
    public static TermValues Commit(TermValues typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        return typed with { Spoken = CommitSpoken(typed.Spoken), Written = CommitWritten(typed.Written) };
    }

    /// <summary>
    /// The commands a row offers. A row the user owns (a custom library's, or a built-in row with no shipped
    /// counterpart) is deleted; an authored built-in row with shipped values is restored instead, since deleting its entry
    /// would only bring the shipped row back at the next load. While the user is typing in a cell or composing with an
    /// IME, Delete and the Space toggle (Turn off, Turn on) never run: the keys belong to the text.
    /// </summary>
    public static TermCommands AvailableCommands(LibraryRow row, bool editingText)
    {
        ArgumentNullException.ThrowIfNull(row);
        var commands = TermCommands.Copy | TermCommands.CopyToDictionary;
        commands |= row.Values.Enabled ? TermCommands.TurnOff : TermCommands.TurnOn | TermCommands.ShowOtherSources;

        switch (row.Origin)
        {
            case TermOrigin.Custom:
                commands |= TermCommands.Delete;
                break;
            case TermOrigin.Added or TermOrigin.NoLongerShipped or TermOrigin.Edited or TermOrigin.Pinned:
                commands |= row.Shipped is null ? TermCommands.Delete : TermCommands.RestoreBuiltIn;
                break;
        }

        if (editingText)
        {
            commands &= ~(TermCommands.Delete | TermCommands.TurnOff | TermCommands.TurnOn);
        }

        return commands;
    }

    /// <summary>
    /// The message shown beside the field an issue is about. <paramref name="spoken"/> is the row's Spoken value, which
    /// the empty-Written and duplicate messages quote; <paramref name="state"/> words a library whose content cannot be
    /// saved; <paramref name="otherSpoken"/> is what the row of <see cref="LibraryValidationIssue.OtherRowId"/> speaks now,
    /// which a duplicate names when it differs: a renamed built-in row still holds its original form.
    /// </summary>
    public static string Message(
        LibraryValidationIssue issue, string? spoken = null, LibraryFileState state = LibraryFileState.Available, string? otherSpoken = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var quoted = LibraryTermKey.Normalize(spoken);
        var other = LibraryTermKey.Normalize(otherSpoken);
        return issue.Kind switch
        {
            LibraryValidationKind.WrittenWithoutSpoken => "Type what you say before how it should be written.",
            LibraryValidationKind.DuplicateSpoken when quoted.Length > 0 && other.Length > 0 && !LibraryTermKey.AreSame(quoted, other) =>
                $"\"{quoted}\" is already in this library as the term you changed to \"{other}\".",
            LibraryValidationKind.DuplicateSpoken => quoted.Length == 0
                ? "This term is already in this library."
                : $"\"{quoted}\" is already in this library.",
            LibraryValidationKind.EmptyWrittenWithoutIntent => quoted.Length == 0
                ? "Type how this should be written."
                : $"Type how \"{quoted}\" should be written.",
            LibraryValidationKind.FieldTooLong =>
                $"This is longer than {LibraryLimits.MaxFieldLength.ToString("N0", CultureInfo.InvariantCulture)} characters. Shorten it to save.",
            LibraryValidationKind.TooManyTerms =>
                $"A library can hold up to {LibraryLimits.MaxTermsPerLibrary.ToString("N0", CultureInfo.InvariantCulture)} terms.",
            LibraryValidationKind.EmptyName => "Type a name for this library.",
            LibraryValidationKind.DuplicateName => "Another library already has this name.",
            LibraryValidationKind.MetadataDoubleQuote or LibraryValidationKind.MetadataUnreadableInOlder => issue.Metadata switch
            {
                LibraryMetadataField.Category => "Categories can't contain a double quote (\"), which older versions of Scribe misread.",
                LibraryMetadataField.Description => "Descriptions can't contain a double quote (\"), which older versions of Scribe misread.",
                _ => MetadataDoubleQuoteMessage,
            },
            LibraryValidationKind.ContentNotSaveable => state switch
            {
                LibraryFileState.PartlyReadable =>
                    "Some rows of this library couldn't be read, so it can't be edited here. Import the file again to see them.",
                LibraryFileState.AwaitingRelease =>
                    "This library is open in another app. Close it there to make changes.",
                _ => "This library couldn't be read, so it can't be edited here.",
            },
            _ => string.Empty,
        };
    }
}
