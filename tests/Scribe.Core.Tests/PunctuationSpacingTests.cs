using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Post-processing drops the space before punctuation that ends a word, and keeps it before a mark
/// that starts one. It used to drop both, so a cleanup model answering "we use .NET" came out as
/// "we use.NET", which also stopped the shipped ".net maui" rule from matching. Run against the
/// shipped libraries that write these terms, so a change to either the rule or the entries shows up.
/// </summary>
public sealed class PunctuationSpacingTests
{
    // The shipped libraries that write .NET, .NET Core and .NET MAUI.
    private static readonly string[] DotNetLibraries = ["dotnet-development", "modern-developer-stack"];

    private sealed class ShippedLibraries(IReadOnlyList<DictionaryEntry> entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];
        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;
        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();
        public void Remove(string id) => throw new NotSupportedException();
    }

    private static (TextPostProcessor Processor, ScribeDatabase Database) Create()
    {
        var database = ScribeDatabase.CreateInMemory();
        var entries = DictionaryLibraryComposer.ComposeLibraries(
            BuiltInDictionaryLibraries.All.Where(library => DotNetLibraries.Contains(library.Id)));
        Assert.Contains(entries, entry => entry.Replacement == ".NET");
        Assert.Contains(entries, entry => entry.Replacement == ".NET Core");
        Assert.Contains(entries, entry => entry.Replacement == ".NET MAUI");

        var processor = new TextPostProcessor(
            new DictionaryRepository(database), NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: new ShippedLibraries(entries));
        return (processor, database);
    }

    [Theory]
    [InlineData("We use .NET for this.", "We use .NET for this.")]
    [InlineData("Build it on .NET Core and .NET MAUI.", "Build it on .NET Core and .NET MAUI.")]
    [InlineData("Port it to .NET 10 , then ship .", "Port it to .NET 10, then ship.")]
    [InlineData("we use .net maui today", "we use .NET MAUI today")]
    [InlineData("Keep the .gitignore and set it to .5 seconds.", "Keep the .gitignore and set it to .5 seconds.")]
    public void A_cleaned_answer_keeps_the_space_before_a_dot_that_starts_a_word(string cleaned, string expected)
    {
        var (processor, database) = Create();
        using (database)
        {
            Assert.Equal(expected, processor.ProcessDetailed(cleaned, "we use dot net").Text);
        }
    }

    [Theory]
    [InlineData("we use dot net", "we use .NET")]
    [InlineData("build it on dot net core and dot net maui", "build it on .NET Core and .NET MAUI")]
    public void The_raw_transcript_takes_the_shipped_entries_with_their_leading_dot(string raw, string expected)
    {
        var (processor, database) = Create();
        using (database)
        {
            Assert.Equal(expected, processor.Process(raw));
        }
    }

    [Theory]
    [InlineData("hello , world", "hello, world")]
    [InlineData("done .", "done.")]
    [InlineData("really ?", "really?")]
    [InlineData("stop !", "stop!")]
    [InlineData("wait ...", "wait...")]
    [InlineData("note : this ; that", "note: this; that")]
    [InlineData("  hello   world  ,  done .  ", "hello world, done.")]
    [InlineData("the end .\" she said", "the end.\" she said")]
    [InlineData("tab\t, then", "tab, then")]
    public void Ordinary_space_before_punctuation_is_still_removed(string text, string expected)
    {
        var (processor, database) = Create();
        using (database)
        {
            Assert.Equal(expected, processor.Process(text));
        }
    }

    [Fact]
    public void Quick_add_repairs_a_transcript_with_the_same_spacing_rule()
    {
        // ApplyRule normalizes the text the same way a dictation does, so the repair keeps .NET's space.
        Assert.Equal(
            "we use .NET MAUI and .NET",
            TextPostProcessor.ApplyRule("we use .NET MAUI and dot net", DictionaryEntry.New("dot net", ".NET")));
    }
}
