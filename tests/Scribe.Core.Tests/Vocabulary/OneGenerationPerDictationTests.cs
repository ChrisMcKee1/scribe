using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// One generation per dictation (plan 3.8, R6): a dictation takes the current vocabulary generation when it is admitted
/// and uses that one for its cleanup and its post-processing, so a Save, a reset of built-in edits or an import that
/// publishes a newer generation while its cleanup call is out changes nothing it sends or writes; the next dictation
/// gets the newer one. Each case runs the real publisher, post-processor and cleanup service, holds the cleanup call
/// at a canary network, publishes the change, and reads what was sent and what was written.
/// </summary>
public sealed class OneGenerationPerDictationTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "the kes trel launch with quill moor at the harbour";

    private readonly ScribeDatabase _database = ScribeDatabase.CreateInMemory();
    private readonly DictionaryRepository _dictionary;
    private readonly TextPostProcessor _processor;

    public OneGenerationPerDictationTests()
    {
        _dictionary = new DictionaryRepository(_database);
        _dictionary.AddRange([Entry("harbour", "Harbour")]);
        _processor = new TextPostProcessor(_dictionary, NullLogger<TextPostProcessor>.Instance);
    }

    public void Dispose() => _database.Dispose();

    public static TheoryData<string> Changes() => ["save", "reset", "import"];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_published_during_a_paused_cleanup_call_leaves_that_dictation_on_the_generation_it_was_admitted_with(string change)
    {
        // Admitted: a custom library spelling "kes trel" as Kestrel, permitted for AI cleanup.
        var before = Of(1, new Library("team", H1, true, Entry("kes trel", "Kestrel")));
        var source = new TestVocabularySource(before);
        using var publisher = new VocabularyPublisher(
            source, _dictionary, _processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        var admitted = (await publisher.StartAsync().WaitAsync(Bound)).Generation;

        // The library vocabulary each change publishes; the same Save also stores a dictionary entry.
        var after = change switch
        {
            // The library's term edited and saved: new content, permitted.
            "save" => Of(2, new Library("team", H2, true, Entry("kes trel", "Kestrelsaved"))),

            // A built-in row reset to its shipped spelling: no edits document left.
            "reset" => Of(2, new Library("team", null, true, Entry("kes trel", "Kestrelshipped"))),

            // Another library imported and permitted, whose rule for the same spoken form ranks first.
            _ => Of(2, new Library("imported", H2, true, Entry("kes trel", "Kestrelimported")), new Library("team", H1, true)),
        };
        var newWritten = after.Entries[0].Replacement;

        await using var harness = new VocabularyCleanupHarness(source);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom());
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanups = 0;
        harness.Network.Respond = async (request, ct) =>
        {
            if (!request.IsProbe && Interlocked.Increment(ref cleanups) == 1)
            {
                await held.Task.WaitAsync(ct);
            }

            return CanaryNetwork.Echo(request);
        };

        // The dictation's cleanup call is out, held at the network, when the change lands and is published.
        var request = harness.Network.Next(sent => !sent.IsProbe);
        var cleaning = harness.Service.Admit(admitted.Cleanup).CleanAsync(Dictated);
        var sentWhileOut = await request.WaitAsync(Bound);
        _dictionary.AddRange([Entry("quill moor", "Quillmoor")]);
        source.Publish(after);
        var published = publisher.Current;
        Assert.NotSame(admitted, published);
        Assert.Same(after, published.Libraries);
        held.SetResult();
        var cleaned = await cleaning.WaitAsync(Bound);

        // What it sent is the admitted generation's vocabulary, none of the newer one's.
        Assert.True(sentWhileOut.Carries("Kestrel (transcribed as"));
        Assert.False(sentWhileOut.Carries(newWritten));
        Assert.False(sentWhileOut.Carries("Quillmoor"));

        // What it writes is the admitted generation's local rules, none of the newer one's.
        var dictation = new DictationPostProcessor(_processor);
        dictation.Use(admitted);
        Assert.Equal("the Kestrel launch with quill moor at the Harbour", dictation.ProcessDetailed(cleaned.Text, Dictated).Text);

        // The next dictation is admitted with the newer generation: it sends and writes the change, dictionary and all.
        harness.Network.Respond = (sent, _) => Task.FromResult(CanaryNetwork.Echo(sent));
        var next = await harness.Service.Admit(published.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);
        Assert.True(harness.Network.Sent[^1].Carries(newWritten));
        Assert.True(harness.Network.Sent[^1].Carries("Quillmoor"));
        dictation.Use(published);
        Assert.Equal($"the {newWritten} launch with Quillmoor at the Harbour", dictation.ProcessDetailed(next.Text, Dictated).Text);
    }

    [Fact]
    public async Task Every_published_generation_is_whole_so_its_rules_glossary_and_scope_come_from_one_snapshot()
    {
        var source = new TestVocabularySource(Of(1, new Library("team", H1, true, Entry("kes trel", "Kestrel"))));
        using var publisher = new VocabularyPublisher(
            source, _dictionary, _processor, NullLogger<VocabularyPublisher>.Instance, work => work());
        await publisher.StartAsync().WaitAsync(Bound);

        for (var generation = 2; generation < 40; generation++)
        {
            var written = "Kestrel" + new string('x', generation);
            source.Publish(Of(generation, new Library("team", H1, true, Entry("kes trel", written))));
            var current = publisher.Current;

            Assert.Equal(generation, current.Libraries.Generation);
            Assert.Same(current.Libraries.AiScope, current.Cleanup.Scope);
            Assert.Contains(written, current.Cleanup.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud), StringComparison.Ordinal);
            Assert.Equal($"the {written} launch at the Harbour", _processor.ProcessDetailed("the kes trel launch at the harbour", null, current.Rules).Text);
        }
    }
}
