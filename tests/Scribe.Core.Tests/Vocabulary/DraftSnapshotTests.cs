using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;
using ScriptedDictionary = Scribe.Core.Tests.Vocabulary.VocabularyPublisherTests.ScriptedDictionary;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// What a Settings save would store, as the draft its wait compares (round 4). Every value is framed, so two different
/// drafts can never hash alike whatever their text contains (A8), and the draft carries an editor's value as typed, before
/// the focus loss that would apply it (A7). The window composes the draft from its controls; what it reads is pinned by
/// <see cref="SaveDraftCoverageTests"/>.
/// </summary>
public sealed class DraftSnapshotTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Every delimiter the drafts have used or a user can type, the tags and digits the framing writes (alone and followed by
    // what a token would hold, so a value can mimic the start of the next token), null and empty.
    private static readonly string?[] Values =
    [
        null, string.Empty, "|", ",", ":", ";", "\u001e", "\u001f", "0", "1:", "s1:a", "n", "s", "as", "sb", "l0:", "p1:a", "t",
        "a", "b", "ab", "a|b", "a,b", "Mail|outlook",
    ];

    [Fact]
    public void Astras_two_profiles_that_joined_into_the_same_text_are_different_drafts()
    {
        var saved = Profile("Mail|outlook", ["outlook"], "Brief.");
        var edited = Profile("Mail", ["outlook"], "outlook|Brief.");

        // Round 3 joined the fields with unescaped delimiters, and both profiles wrote exactly the same text.
        Assert.Equal(Joined(saved), Joined(edited));
        Assert.Equal("Mail|outlook|outlook|Brief.|", Joined(saved));

        Assert.NotEqual(new DraftSnapshot().Profiles([saved]).Hash(), new DraftSnapshot().Profiles([edited]).Hash());
    }

    [Fact]
    public void Distinct_values_never_write_the_same_draft_whatever_delimiters_they_contain()
    {
        // Two values in a row: every ordered pair of different values gives a different draft.
        AssertDistinct(
            from first in Values
            from second in Values
            select (Key: Key(first, second), Hash: new DraftSnapshot().Text(first).Text(second).Hash()));

        // Lists of up to two values, then a value after the list or nothing: nothing moves between a list and what follows it,
        // so a list followed by a value never matches a longer list.
        var lists = new List<string?[]> { Array.Empty<string?>() };
        lists.AddRange(Values.Select(value => new[] { value }));
        lists.AddRange(from first in Values from second in Values select new[] { first, second });
        AssertDistinct(
            from list in lists
            from after in new string?[] { null, string.Empty, "a", "a,b", "(nothing)" }
            select (
                Key: Key([.. list, "#", after]),
                Hash: after == "(nothing)" ? new DraftSnapshot().List(list).Hash() : new DraftSnapshot().List(list).Text(after).Hash()));

        // Profiles, field by field, including the process list and a missing line-break override.
        var names = new[] { "Mail", "Mail|outlook", string.Empty };
        var processes = new[] { Array.Empty<string>(), ["outlook"], ["outlook", "teams"], ["outlook,teams"] };
        var styles = new string?[] { null, string.Empty, "Brief.", "outlook|Brief." };
        var newlines = new NewlineInjectionMode?[] { null, NewlineInjectionMode.KeepNewlines };
        AssertDistinct(
            from name in names
            from list in processes
            from style in styles
            from newline in newlines
            select (
                Key: Key([name, string.Join("\u0001", list), style, newline?.ToString()]),
                Hash: new DraftSnapshot().Profiles([Profile(name, list, style, newline)]).Hash()));

        // The subscription a save stores, and a missing one against one with every field empty.
        var fields = new string?[] { null, string.Empty, "a|b", "a", "b" };
        var subscriptions =
            (from id in fields.OfType<string>()
             from name in fields.OfType<string>()
             from tenant in fields.OfType<string>()
             select (Key: Key(id, name, tenant), Hash: new DraftSnapshot().Subscription(new AzureSubscription(id, name, tenant)).Hash()))
            .Append((Key: "none", Hash: new DraftSnapshot().Subscription(null).Hash()));
        AssertDistinct(subscriptions);

        // A hotkey's name, and no hotkey against one.
        var binding = HotkeyBinding.DefaultDictation;
        AssertDistinct(
            new[] { null, "Page Down", "Page, Down", "Page Down, SecondaryVirtualKey = 34" }
                .Select(name => (Key: name ?? "(null)", Hash: new DraftSnapshot().Binding(binding with { DisplayName = name }).Hash()))
                .Append((Key: "none", Hash: new DraftSnapshot().Binding(null).Hash())));

        // Parts never run into the values after them, or into nothing after them.
        AssertDistinct(
            from part in new[] { "a", "ab", "as1:b" }
            from value in new string?[] { "b", string.Empty, "(end)" }
            select (
                Key: Key(part, value),
                Hash: value == "(end)" ? new DraftSnapshot().Part(part).Hash() : new DraftSnapshot().Part(part).Text(value).Hash()));
    }

    [Fact]
    public void Null_differs_from_empty_and_every_kind_of_value_is_framed()
    {
        Assert.NotEqual(new DraftSnapshot().Text(null).Hash(), new DraftSnapshot().Text(string.Empty).Hash());
        Assert.NotEqual(new DraftSnapshot().List([null]).Hash(), new DraftSnapshot().List([string.Empty]).Hash());
        Assert.NotEqual(new DraftSnapshot().List([]).Hash(), new DraftSnapshot().List([string.Empty]).Hash());
        Assert.NotEqual(new DraftSnapshot().Flag((bool?)null).Hash(), new DraftSnapshot().Flag(false).Hash());
        Assert.NotEqual(new DraftSnapshot().Number((double?)null).Hash(), new DraftSnapshot().Number(0d).Hash());
        Assert.NotEqual(new DraftSnapshot().Number(1).Number(23).Hash(), new DraftSnapshot().Number(12).Number(3).Hash());
        Assert.NotEqual(new DraftSnapshot().Number((long?)null).Hash(), new DraftSnapshot().Number(0).Hash());
        Assert.NotEqual(new DraftSnapshot().Text("1").Hash(), new DraftSnapshot().Number(1).Hash());

        // The microphone a save stores is normalized the way it stores it: a blank ID is the Windows default.
        Assert.Equal(
            new DraftSnapshot().Microphone(new MicrophoneSelection(" ", "USB")).Hash(),
            new DraftSnapshot().Microphone(MicrophoneSelection.WindowsDefault).Hash());
        Assert.NotEqual(
            new DraftSnapshot().Microphone(new MicrophoneSelection("a|b", "c")).Hash(),
            new DraftSnapshot().Microphone(new MicrophoneSelection("a", "b|c")).Hash());

        // The enabled libraries are the set a save writes: order and case do not change it, and an id does.
        Assert.Equal(new DraftSnapshot().LibrarySet(["Team", "dotnet"]).Hash(), new DraftSnapshot().LibrarySet(["DOTNET", "team"]).Hash());
        Assert.NotEqual(new DraftSnapshot().LibrarySet(["team"]).Hash(), new DraftSnapshot().LibrarySet(["team", "dotnet"]).Hash());
    }

    [Fact]
    public void The_hash_keeps_every_code_unit_and_the_same_values_always_hash_alike()
    {
        // A lone surrogate survives: an encoding that replaced it would make these two drafts hash alike.
        Assert.NotEqual(new DraftSnapshot().Text("\uD800").Hash(), new DraftSnapshot().Text("\uD801").Hash());

        var profiles = new[] { Profile("Mail", ["outlook"], "Brief.") };
        Assert.Equal(
            new DraftSnapshot().Part("page").Text("a").Flag(true).Number(3).Number(2.5).List(["x"]).Profiles(profiles).Hash(),
            new DraftSnapshot().Part("page").Text("a").Flag(true).Number(3).Number(2.5).List(["x"]).Profiles(profiles).Hash());
        Assert.Matches("^[0-9A-F]{64}$", new DraftSnapshot().Hash());
    }

    [Fact]
    public async Task A_model_name_typed_but_not_yet_applied_keeps_a_held_save_open()
    {
        // Astra's A7 sequence, with the Save's build held. The window's AI page as its draft reads it: the model picker's
        // text (applied to the endpoint, the deployment name and the subscription only when the picker loses focus), and
        // the subscription those resolve to.
        var dictionary = new ScriptedDictionary([]);
        var queued = new List<Action>();
        using var publisher = new VocabularyPublisher(
            new TestVocabularySource(Of(3)),
            dictionary,
            new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance),
            NullLogger<VocabularyPublisher>.Instance,
            queued.Add);
        var starting = publisher.StartAsync();
        Assert.Single(queued)();
        queued.Clear();
        await starting.WaitAsync(Bound);

        var page = new AzurePage("gpt-5  in  Contoso", "https://contoso.example/", "gpt-5", new AzureSubscription("sub-1", "Contoso", "tenant-1"));
        var withPicker = StoredChangeAcknowledgement.Watch(publisher.RefreshAsync(), () => page.Draft(readPicker: true));
        var withoutPicker = StoredChangeAcknowledgement.Watch(publisher.RefreshAsync(), () => page.Draft(readPicker: false));
        var completing = withPicker.CompleteAsync();
        var closingOver = withoutPicker.CompleteAsync();

        // Another deployment's full name typed into the still-editable window, no item selected, and focus still there:
        // nothing has applied it to the endpoint, the deployment name or the subscription yet.
        page.PickerText = "gpt-5-mini  in  Fabrikam";
        Assert.Single(queued)();

        Assert.Equal(StoredChangeOutcome.ChangedWhileSaving, await completing.WaitAsync(Bound));

        // Round 3's draft, which left the picker to the subscription, saw nothing and would have closed over the typing.
        Assert.Equal(StoredChangeOutcome.InEffect, await closingOver.WaitAsync(Bound));
    }

    private static AppProfile Profile(string name, string[] processes, string? style, NewlineInjectionMode? newline = null) =>
        new() { Name = name, ProcessNames = [.. processes], WritingStyle = style, NewlineHandling = newline };

    // Round 3's profile part, for the counterexample.
    private static string Joined(AppProfile profile) =>
        $"{profile.Name}|{string.Join(',', profile.ProcessNames)}|{profile.WritingStyle}|{profile.NewlineHandling}";

    // A key no two different tuples share: each value's length in front of it, null marked.
    private static string Key(params string?[] values) =>
        string.Concat(values.Select(value => value is null ? "~" : $"{value.Length}:{value}"));

    private static void AssertDistinct(IEnumerable<(string Key, string Hash)> drafts)
    {
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, hash) in drafts)
        {
            Assert.True(keys.Add(key), $"The test listed the same values twice: {key}");
            if (byHash.TryGetValue(hash, out var other))
            {
                Assert.Fail($"Two different drafts hashed alike: {other} and {key}");
            }

            byHash.Add(hash, key);
        }

        Assert.True(byHash.Count > 1);
    }

    // The window's AI page as the draft reads it, with only what A7 needs.
    private sealed class AzurePage(string picker, string endpoint, string deployment, AzureSubscription applied)
    {
        public string PickerText { get; set; } = picker;

        public string Draft(bool readPicker)
        {
            var draft = new DraftSnapshot().Part("SectionAi");
            if (readPicker)
            {
                draft.Part("editable").Text(PickerText);
            }

            draft.Part("text").Text(endpoint).Part("text").Text(deployment);
            return draft.Part("subscription")
                .Subscription(AzureSubscriptionSelection.ResolveAuthenticationSubscription(null, applied, endpoint, deployment))
                .Hash();
        }
    }
}
