using Scribe.Core.Libraries;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>How the editor commits typed values, which commands a row offers, and its messages (3.5.2).</summary>
public sealed class LibraryEditorTests
{
    [Theory]
    [InlineData("  get   hub ", "get hub")]
    [InlineData("get\u00A0\thub", "get hub")]
    [InlineData("Get Hub", "Get Hub")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void A_typed_spoken_value_is_committed_trimmed_and_collapsed_with_case_kept(string? typed, string committed)
    {
        Assert.Equal(committed, LibraryEditor.CommitSpoken(typed));
        Assert.True(LibraryTermKey.IsInCommitForm(LibraryEditor.CommitSpoken(typed)));
    }

    [Fact]
    public void A_typed_written_value_is_only_trimmed_and_keeps_its_inner_lines_and_spacing()
    {
        Assert.Equal("Line one\r\n  Line  two", LibraryEditor.CommitWritten("  Line one\r\n  Line  two \t"));
        Assert.Equal(string.Empty, LibraryEditor.CommitWritten(null));
        Assert.Equal(new TermValues("get hub", "GitHub", false, false), LibraryEditor.Commit(new TermValues(" get  hub", " GitHub ", false, false)));
    }

    [Fact]
    public void Committing_changes_keeps_every_untouched_field_exactly_as_the_row_shows_it()
    {
        var displayed = new TermValues("get  hub", " GitHub", WholeWord: true, Enabled: true);

        // Only Written was typed into: Spoken stays as shown, even though it is not in commit form.
        Assert.Equal(new TermValues("get  hub", "GH", true, true), LibraryEditor.CommitChanges(displayed, displayed with { Written = " GH " }));

        // A field typed back to what commits to the shown value is unchanged, and so is one passed back as shown.
        Assert.Equal(displayed, LibraryEditor.CommitChanges(displayed, displayed));
        Assert.Equal(displayed with { WholeWord = false }, LibraryEditor.CommitChanges(displayed, displayed with { WholeWord = false }));
        Assert.Equal(
            new TermValues("get hub", " GitHub", true, true),
            LibraryEditor.CommitChanges(displayed, displayed with { Spoken = " get hub " }));
    }

    [Fact]
    public void Ill_formed_utf16_is_told_apart_from_text()
    {
        // Built in code: theory data is serialized between the runner and the test, which replaces a lone surrogate.
        var cases = new (string? Text, bool WellFormed)[]
        {
            ("plain", true),
            ("rocket \uD83D\uDE80 launch", true),
            ("", true),
            (null, true),
            ("\uD83D", false),
            ("half \uD83D of it", false),
            ("\uDE80", false),
            ("backwards \uDE80\uD83D", false),
            ("pair then half \uD83D\uDE80\uD83D", false),

            // A high surrogate as the last character, with nothing after it to pair with (the contract's rule for it).
            ("GitHub\uD83D", false),
            ("\uD83D\uD83D\uDE80", false),
        };

        foreach (var (text, wellFormed) in cases)
        {
            Assert.Equal(wellFormed, LibraryEditor.IsWellFormed(text));
        }
    }

    [Fact]
    public void Each_row_offers_its_honest_commands_and_none_of_the_keys_while_text_is_edited()
    {
        var shipped = new TermValues("get hub", "GitHub");
        var key = LibraryTermKey.From("get hub");
        var custom = LibraryRow.Custom(shipped);
        var builtIn = new LibraryRow(key, shipped, TermOrigin.Shipped, shipped);
        var edited = new LibraryRow(key, shipped with { Written = "GH" }, TermOrigin.Edited, shipped,
            new BuiltInTermEdit(key, BuiltInTermIntent.Edited, shipped, shipped with { Written = "GH" }));
        var off = new LibraryRow(key, shipped with { Enabled = false }, TermOrigin.Off, shipped,
            new BuiltInTermEdit(key, BuiltInTermIntent.Off, shipped, null));
        var added = new LibraryRow(key, shipped, TermOrigin.Added, null, new BuiltInTermEdit(key, BuiltInTermIntent.Added, null, shipped));
        const TermCommands copies = TermCommands.Copy | TermCommands.CopyToDictionary;

        Assert.Equal(copies | TermCommands.TurnOff | TermCommands.Delete, LibraryEditor.AvailableCommands(custom, editingText: false));
        Assert.Equal(copies | TermCommands.TurnOff, LibraryEditor.AvailableCommands(builtIn, editingText: false));
        Assert.Equal(copies | TermCommands.TurnOff | TermCommands.RestoreBuiltIn, LibraryEditor.AvailableCommands(edited, editingText: false));
        Assert.Equal(copies | TermCommands.TurnOn | TermCommands.ShowOtherSources, LibraryEditor.AvailableCommands(off, editingText: false));
        Assert.Equal(copies | TermCommands.TurnOff | TermCommands.Delete, LibraryEditor.AvailableCommands(added, editingText: false));

        foreach (var row in new[] { custom, builtIn, edited, off, added })
        {
            var editing = LibraryEditor.AvailableCommands(row, editingText: true);
            Assert.False(editing.HasFlag(TermCommands.Delete));
            Assert.False(editing.HasFlag(TermCommands.TurnOff));
            Assert.False(editing.HasFlag(TermCommands.TurnOn));
            Assert.True(editing.HasFlag(TermCommands.Copy));
        }
    }

    [Fact]
    public void The_messages_quote_the_term_and_name_the_limits()
    {
        LibraryValidationIssue Issue(LibraryValidationKind kind, LibraryMetadataField field = LibraryMetadataField.None) =>
            new("team-terms", 1, kind, TermFields.None, field);

        Assert.Equal("Type how \"get hub\" should be written.", LibraryEditor.Message(Issue(LibraryValidationKind.EmptyWrittenWithoutIntent), " get  hub "));
        Assert.Equal("\"kube\" is already in this library.", LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateSpoken), "kube"));
        Assert.Equal("\"kube\" is already in this library.", LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateSpoken), "kube", otherSpoken: "KUBE"));
        Assert.Equal(
            "\"get hub\" is already in this library as the term you changed to \"git hub\".",
            LibraryEditor.Message(Issue(LibraryValidationKind.DuplicateSpoken), "get hub", otherSpoken: "git  hub"));
        Assert.Equal("This is longer than 2,000 characters. Shorten it to save.", LibraryEditor.Message(Issue(LibraryValidationKind.FieldTooLong)));
        Assert.Equal("A library can hold up to 50,000 terms.", LibraryEditor.Message(Issue(LibraryValidationKind.TooManyTerms)));
        Assert.Equal(LibraryEditor.MetadataDoubleQuoteMessage, LibraryEditor.Message(Issue(LibraryValidationKind.MetadataDoubleQuote, LibraryMetadataField.Name)));
        Assert.StartsWith("Descriptions can't", LibraryEditor.Message(Issue(LibraryValidationKind.MetadataUnreadableInOlder, LibraryMetadataField.Description)));
        Assert.Contains("Import the file again", LibraryEditor.Message(Issue(LibraryValidationKind.ContentNotSaveable), state: LibraryFileState.PartlyReadable));
        Assert.Contains("open in another app", LibraryEditor.Message(Issue(LibraryValidationKind.ContentNotSaveable), state: LibraryFileState.AwaitingRelease));
        foreach (var kind in Enum.GetValues<LibraryValidationKind>())
        {
            var message = LibraryEditor.Message(Issue(kind), "term");
            Assert.NotEmpty(message);
            Assert.DoesNotContain('\u2014', message);
            Assert.DoesNotContain('\u2013', message);
        }
    }
}
