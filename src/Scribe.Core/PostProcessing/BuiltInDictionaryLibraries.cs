using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// The dictionary libraries that ship inside the app, loaded once from CSV files embedded as
/// resources under <c>PostProcessing/Libraries</c>. Each file's comment header supplies its display
/// name, category, and description; the stable library id is the file name without its extension
/// (e.g. <c>microsoft-azure</c>), which is what the enabled-set in settings references.
/// </summary>
public static class BuiltInDictionaryLibraries
{
    private const string ResourcePrefix = "Scribe.Core.PostProcessing.Libraries.";
    private const string ResourceSuffix = ".csv";

    private static readonly Lazy<IReadOnlyList<DictionaryLibrary>> Cached = new(Load);

    /// <summary>All built-in libraries, ordered by category then name.</summary>
    public static IReadOnlyList<DictionaryLibrary> All => Cached.Value;

    private static IReadOnlyList<DictionaryLibrary> Load()
    {
        var assembly = typeof(BuiltInDictionaryLibraries).Assembly;
        var libraries = new List<DictionaryLibrary>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var id = resource[ResourcePrefix.Length..^ResourceSuffix.Length];
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var textReader = new StreamReader(stream);
            var file = DictionaryLibraryCsv.Parse(textReader.ReadToEnd());
            if (file.Entries.Count == 0)
            {
                continue;
            }

            libraries.Add(new DictionaryLibrary(
                id,
                file.Name ?? Humanize(id),
                file.Category ?? "General",
                file.Description,
                BuiltIn: true,
                file.Entries));
        }

        return libraries
            .OrderBy(l => l.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // "microsoft-azure" -> "Microsoft Azure": a readable fallback when a file omits its name header,
    // and reused by the custom-library loader for imported files without a name.
    internal static string Humanize(string id)
    {
        var words = id
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var joined = string.Join(' ', words);
        return joined.Length == 0 ? id : joined;
    }
}
