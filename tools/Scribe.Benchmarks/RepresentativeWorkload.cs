using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

public enum WorkloadTextLength
{
    /// <summary>One spoken sentence plus a snippet trigger, about 30 words.</summary>
    Short,

    /// <summary>A long dictation, about 550 words (several minutes of speech).</summary>
    Long,
}

public enum WorkloadDictionary
{
    /// <summary>The first-run seed vocabulary plus a handful of personal entries (20 rules).</summary>
    Small,

    /// <summary>The small dictionary plus every built-in library switched on (about 1,500 rules).</summary>
    Large,
}

public enum SourceShape
{
    /// <summary>
    /// The text is the raw transcript and the source is the same string: what the pipeline passes
    /// when AI cleanup is off or skipped.
    /// </summary>
    SameAsText,

    /// <summary>The text is a cleaned transcript and the source is the raw recognizer output.</summary>
    RawTranscript,
}

/// <summary>
/// Synthetic but shape-realistic post-processing inputs shared by the benchmarks and the soak
/// harness. Every string here is invented; nothing is read from a user's dictionary or history.
/// </summary>
internal static class RepresentativeWorkload
{
    private const string SnippetTrigger = "insert my signature";

    private static readonly (string Raw, string Cleaned)[] Sentences =
    [
        ("so i pushed the dot net api changes to github and the azure devops pipeline ran the tests before the blazor front end deployed",
         "So I pushed the .NET API changes to GitHub, and the Azure DevOps pipeline ran the tests before the Blazor front end deployed."),
        ("the kubernetes cluster pulls the docker image from the registry and the key vault holds the connection string for cosmos db",
         "The Kubernetes cluster pulls the Docker image from the registry, and the Key Vault holds the connection string for Cosmos DB."),
        ("we moved the python notebooks into vs code and wired the open ai client through semantic kernel with a small rag index",
         "We moved the Python notebooks into VS Code and wired the OpenAI client through Semantic Kernel with a small RAG index."),
        ("please review the type script changes in the next js app and the graph ql schema before the node js service ships",
         "Please review the TypeScript changes in the Next.js app and the GraphQL schema before the Node.js service ships."),
        ("the terraform plan and the bicep template both create the app service and the postgres server in the same region",
         "The Terraform plan and the Bicep template both create the App Service and the Postgres server in the same region."),
        ("after that the power bi report reads the json export and posts a summary to teams and sharepoint for the gpt review",
         "After that, the Power BI report reads the JSON export and posts a summary to Teams and SharePoint for the GPT review."),
    ];

    private static readonly DictionaryEntry[] PersonalEntries =
    [
        DictionaryEntry.New("azure", "Azure"),
        DictionaryEntry.New("azure devops", "Azure DevOps"),
        DictionaryEntry.New("blazor", "Blazor"),
        DictionaryEntry.New("kubernetes", "Kubernetes"),
        DictionaryEntry.New("docker", "Docker"),
        DictionaryEntry.New("key vault", "Key Vault"),
        DictionaryEntry.New("cosmos db", "Cosmos DB"),
        DictionaryEntry.New("vs code", "VS Code"),
        DictionaryEntry.New("type script", "TypeScript"),
        DictionaryEntry.New("next js", "Next.js"),
        DictionaryEntry.New("graph ql", "GraphQL"),
        DictionaryEntry.New("power bi", "Power BI"),
        DictionaryEntry.New("json", "JSON"),
    ];

    private static readonly Snippet[] SnippetSet =
    [
        Snippet.New(SnippetTrigger, "Regards,\nThe platform team"),
        Snippet.New("insert standup template", "Yesterday:\nToday:\nBlockers: none"),
        Snippet.New("insert meeting link", "Join the call from the calendar invite."),
        Snippet.New("insert release checklist",
            "Check the azure devops pipeline, the dot net api tests, and the github release notes."),
        Snippet.New("insert office address", "Building 1, Example Way"),
    ];

    public static int SentencesPerLongText => Sentences.Length * 4;

    public static TextPostProcessor CreatePostProcessor(
        WorkloadDictionary dictionary, bool snippets, bool reuseIdenticalSourceScan = true)
    {
        var baseEntries = DefaultVocabulary.Entries.Concat(PersonalEntries).ToArray();
        var libraries = dictionary == WorkloadDictionary.Large
            ? DictionaryLibraryComposer.ComposeLibraries(BuiltInDictionaryLibraries.All)
            : [];

        var processor = new TextPostProcessor(
            new DictionaryStub(baseEntries),
            NullLogger<TextPostProcessor>.Instance,
            snippets ? new SnippetStub(SnippetSet) : null,
            new LibraryStub(libraries))
        {
            ReuseIdenticalSourceScan = reuseIdenticalSourceScan,
        };
        processor.Reload();
        return processor;
    }

    /// <summary>
    /// Raw recognizer-shaped text. Always ends with a snippet trigger so the snippets-on arms expand
    /// it while the snippets-off arms leave the same words untouched.
    /// </summary>
    public static string RawTranscript(WorkloadTextLength length) =>
        string.Join(' ', Pick(length).Select(sentence => sentence.Raw).Append(SnippetTrigger));

    /// <summary>What AI cleanup plausibly returns for <see cref="RawTranscript"/>.</summary>
    public static string CleanedTranscript(WorkloadTextLength length) =>
        string.Join(' ', Pick(length).Select(sentence => sentence.Cleaned).Append("Insert my signature."));

    private static IEnumerable<(string Raw, string Cleaned)> Pick(WorkloadTextLength length) =>
        length == WorkloadTextLength.Short
            ? Sentences.Take(1)
            : Enumerable.Range(0, SentencesPerLongText).Select(index => Sentences[index % Sentences.Length]);

    internal sealed class DictionaryStub(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> GetAll() => entries;
        public IReadOnlyList<DictionaryEntry> GetEnabled() => entries;
        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();
        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) =>
            throw new NotSupportedException();
        public void Update(DictionaryEntry entry) => throw new NotSupportedException();
        public void Delete(long id) => throw new NotSupportedException();
        public void SaveAll(IReadOnlyList<DictionaryEntry> updatedEntries) => throw new NotSupportedException();
        public int SeedIfEmpty(IEnumerable<DictionaryEntry> seedEntries) => throw new NotSupportedException();
        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> retiredEntries) => throw new NotSupportedException();
    }

    private sealed class SnippetStub(IReadOnlyList<Snippet> snippets) : ISnippetRepository
    {
        public IReadOnlyList<Snippet> GetAll() => snippets;
        public IReadOnlyList<Snippet> GetEnabled() => snippets;
        public void SaveAll(IReadOnlyList<Snippet> updated) => throw new NotSupportedException();
    }

    private sealed class LibraryStub(IReadOnlyList<DictionaryEntry> entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];
        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;
        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();
        public void Remove(string id) => throw new NotSupportedException();
    }
}
