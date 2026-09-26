using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.CleanupLogging;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// A library vocabulary republished at the same library generation (contract 9.4, the storage stream's round 4): while
/// committed content cannot be read right now the library service holds its libraries back, and once it can it restores
/// them, both at the stored generation, so <see cref="LibraryVocabulary.Generation"/> says nothing about whether the
/// vocabulary changed. Every such publication reaches the next dictation: while libraries are held back it runs on the
/// personal dictionary alone, with nothing failing, and the first publication after the hold-back brings the libraries
/// back to local replacement and the glossary without a restart.
/// </summary>
public sealed class SameGenerationRepublicationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const long StoredGeneration = 7;
    private const string Dictated = "please ask zeb ra quill about lan tern ridge";

    // Letters, spaces and a parenthesis only, which JSON escaping leaves as they are, so a request body can never hide them.
    private const string LibraryTerm = "Zebraquill (transcribed as";
    private const string DictionaryTerm = "Lanternridge (transcribed as";

    [Fact]
    public async Task A_library_held_back_and_restored_at_the_same_generation_is_followed_by_local_replacement_and_the_glossary()
    {
        var dictionary = new VocabularyPublisherTests.ScriptedDictionary([Entry("lan tern ridge", "Lanternridge")]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance);
        var source = new TestVocabularySource(Whole());
        var publisherLog = new CapturingLogger<VocabularyPublisher>();
        using var publisher = new VocabularyPublisher(source, dictionary, processor, publisherLog, work => work());
        var whole = (await publisher.StartAsync().WaitAsync(Bound)).Generation;
        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var dictation = new DictationPostProcessor(processor);

        // Whole: the library replaces locally, and its term goes in the glossary beside the dictionary's.
        var first = await DictateAsync(harness, dictation, whole);
        Assert.True(first.Sent.Carries(LibraryTerm));
        Assert.True(first.Sent.Carries(DictionaryTerm));
        Assert.Equal("please ask Zebraquill about Lanternridge", first.Written);

        // Held back at the generation it already had: no library entries and an empty scope. The publication is taken
        // all the same, and the next dictation runs on the personal dictionary alone, with nothing failing or warned.
        var logMark = (Publisher: publisherLog.Entries.Count, Cleanup: harness.Log.Entries.Count);
        var heldBack = Of(StoredGeneration);
        source.Publish(heldBack);
        var duringHoldBack = publisher.Current;
        Assert.Same(heldBack, duringHoldBack.Libraries);
        Assert.Equal(whole.Libraries.Generation, duringHoldBack.Libraries.Generation);
        Assert.Equal(whole.Number + 1, duringHoldBack.Number);
        Assert.Empty(duringHoldBack.AiScope.PermittedLibraryIds);
        Assert.Equal(["lan tern ridge"], duringHoldBack.GlossaryEntries.Select(entry => entry.Pattern));

        var second = await DictateAsync(harness, dictation, duringHoldBack);
        Assert.Equal(CleanupOutcome.Unchanged, second.Cleaned.Outcome);
        Assert.Null(second.Cleaned.FailureReason);
        Assert.False(second.Sent.Carries("Zebraquill"));
        Assert.True(second.Sent.Carries(DictionaryTerm));
        Assert.Equal("please ask zeb ra quill about Lanternridge", second.Written);
        var handOff = source.HandOffs[^1];
        Assert.Same(heldBack.AiScope, handOff.Admitted);
        Assert.True(handOff.HandedOver);

        // A dictation admitted before the hold-back whose request would leave during it is held back, and a held-back
        // request is no failure: local rules finish it.
        var sentBefore = harness.Network.Sent.Count;
        var straddling = await harness.Service.Admit(whole.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Skipped, straddling.Outcome);
        Assert.Equal(Dictated, straddling.Text);
        Assert.Null(straddling.FailureReason);
        Assert.Equal(sentBefore, harness.Network.Sent.Count);

        Assert.DoesNotContain(publisherLog.Entries.Skip(logMark.Publisher), entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(harness.Log.Entries.Skip(logMark.Cleanup), entry => entry.Level >= LogLevel.Warning);

        // Restored at the same generation again, as a new vocabulary equal to the one held back: the first publication
        // after the hold-back brings the library back to local replacement and the glossary.
        var restored = Whole();
        source.Publish(restored);
        var afterRestore = publisher.Current;
        Assert.Same(restored, afterRestore.Libraries);
        Assert.Equal(StoredGeneration, afterRestore.Libraries.Generation);
        Assert.Equal(duringHoldBack.Number + 1, afterRestore.Number);

        var third = await DictateAsync(harness, dictation, afterRestore);
        Assert.Equal(CleanupOutcome.Unchanged, third.Cleaned.Outcome);
        Assert.True(third.Sent.Carries(LibraryTerm));
        Assert.True(third.Sent.Carries(DictionaryTerm));
        Assert.Equal("please ask Zebraquill about Lanternridge", third.Written);
    }

    [Fact]
    public async Task A_restoration_published_at_the_same_generation_while_a_build_runs_is_built_after_it_and_never_dropped()
    {
        // A fresh start that could read no committed manifest holds every library back; the restoration is published
        // while a generation is being built from the held-back vocabulary.
        var heldBack = Of(StoredGeneration);
        var source = new TestVocabularySource(heldBack);
        var dictionary = new VocabularyPublisherTests.ScriptedDictionary([Entry("lan tern ridge", "Lanternridge")]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance);
        using var publisher = new VocabularyPublisher(
            source, dictionary, processor, NullLogger<VocabularyPublisher>.Instance, work => _ = Task.Run(work));
        Assert.Same(heldBack, (await publisher.StartAsync().WaitAsync(Bound)).Generation.Libraries);

        var restored = Whole();
        var restoredPublished = new TaskCompletionSource<VocabularyGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Published += generation =>
        {
            if (ReferenceEquals(generation.Libraries, restored))
            {
                restoredPublished.TrySetResult(generation);
            }
        };

        // Hold the next build after it has read the held-back vocabulary (a build reads the library snapshot before the
        // dictionary), and publish the restoration then.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        dictionary.Read = () =>
        {
            dictionary.Read = null;
            reading.TrySetResult();
            release.Wait(Bound);
        };

        var building = publisher.RefreshAsync();
        await reading.Task.WaitAsync(Bound);
        source.Publish(restored);
        release.Set();

        // The build that was running publishes what it read; the restoration is built after it, not dropped for having
        // the generation that build already had.
        var answered = (await building.WaitAsync(Bound)).Generation;
        Assert.Same(heldBack, answered.Libraries);
        var afterRestore = await restoredPublished.Task.WaitAsync(Bound);
        Assert.True(afterRestore.Number > answered.Number);
        Assert.Same(afterRestore, publisher.Current);
        Assert.Equal(StoredGeneration, afterRestore.Libraries.Generation);
        Assert.Equal(
            "please ask Zebraquill about Lanternridge",
            processor.ProcessDetailed(Dictated, null, afterRestore.Rules).Text);
        Assert.Contains("Zebraquill", afterRestore.Cleanup.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud), StringComparison.Ordinal);
    }

    // The library as the service publishes it whole for the stored generation, permitted for AI cleanup; a new instance
    // each time, as each publication is.
    private static LibraryVocabulary Whole() =>
        Of(StoredGeneration, new Library("team", H1, true, Entry("zeb ra quill", "Zebraquill")));

    // One dictation as the controller runs it: admitted with a generation, cleaned under it, then its dictionary pass
    // over the same generation. Returns the request that reached the network and what would be typed.
    private static async Task<(CleanupResult Cleaned, SentRequest Sent, string Written)> DictateAsync(
        VocabularyCleanupHarness harness, DictationPostProcessor dictation, VocabularyGeneration admitted)
    {
        var sentBefore = harness.Network.Sent.Count;
        var cleaned = await harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        var sent = Assert.Single(harness.Network.Sent.Skip(sentBefore), request => !request.IsProbe);
        dictation.Use(admitted);
        return (cleaned, sent, dictation.ProcessDetailed(cleaned.Text, Dictated).Text);
    }
}
