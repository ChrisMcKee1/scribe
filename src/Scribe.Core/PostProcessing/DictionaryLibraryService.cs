using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <inheritdoc cref="IDictionaryLibraryService"/>
public sealed class DictionaryLibraryService : IDictionaryLibraryService
{
    private readonly AppPaths _paths;
    private readonly ISettingsRepository _settings;
    private readonly ILogger<DictionaryLibraryService> _logger;

    public DictionaryLibraryService(
        AppPaths paths, ISettingsRepository settings, ILogger<DictionaryLibraryService> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<DictionaryLibrary> GetLibraries()
    {
        var result = new List<DictionaryLibrary>(BuiltInDictionaryLibraries.All);
        result.AddRange(LoadCustom());
        return result;
    }

    public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries()
    {
        var enabled = _settings.Load().EnabledDictionaryLibraryIds;
        if (enabled is null || enabled.Count == 0)
        {
            return [];
        }

        var ids = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var libraries = GetLibraries().Where(l => ids.Contains(l.Id));
        return DictionaryLibraryComposer.ComposeLibraries(libraries);
    }

    public DictionaryLibrary Import(string csv, string? suggestedName)
    {
        var file = DictionaryLibraryCsv.Parse(csv);
        if (file.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                "That library contains invalid CSV rows:\n" + string.Join("\n", file.Errors.Take(5)));
        }

        if (file.Entries.Count == 0)
        {
            throw new InvalidOperationException(
                "That file has no usable dictionary rows. Each row needs at least a spoken form and a replacement.");
        }

        var name = file.Name
            ?? (string.IsNullOrWhiteSpace(suggestedName) ? null : suggestedName.Trim())
            ?? "Imported library";
        var category = file.Category ?? "Custom";

        Directory.CreateDirectory(_paths.LibrariesDir);
        var id = UniqueId(Slugify(name));
        var library = new DictionaryLibrary(id, name, category, file.Description, BuiltIn: false, file.Entries);

        // Re-export through the library writer so the stored file is normalized and always carries a
        // header, regardless of what the source file looked like. Written UTF-8 without a BOM (the
        // File.WriteAllText default) so the '#' header is detected cleanly on the next read.
        File.WriteAllText(Path.Combine(_paths.LibrariesDir, id + ".csv"), DictionaryLibraryCsv.Export(library));
        _logger.LogInformation("Imported dictionary library '{Name}' ({Count} entries) as {Id}.",
            name, file.Entries.Count, id);
        return library;
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (BuiltInDictionaryLibraries.All.Any(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Built-in libraries can't be removed. Turn it off instead.");
        }

        // Guard against a caller-supplied id escaping the libraries folder.
        var fileName = id + ".csv";
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("That library id is not valid.");
        }

        var path = Path.Combine(_paths.LibrariesDir, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Removed custom dictionary library {Id}.", id);
        }
    }

    private List<DictionaryLibrary> LoadCustom()
    {
        var libraries = new List<DictionaryLibrary>();
        if (!Directory.Exists(_paths.LibrariesDir))
        {
            return libraries;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_paths.LibrariesDir, "*.csv");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate the dictionary libraries folder.");
            return libraries;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var file = DictionaryLibraryCsv.Parse(File.ReadAllText(path));
                if (file.Entries.Count == 0)
                {
                    continue;
                }

                libraries.Add(new DictionaryLibrary(
                    id,
                    file.Name ?? BuiltInDictionaryLibraries.Humanize(id),
                    file.Category ?? "Custom",
                    file.Description,
                    BuiltIn: false,
                    file.Entries));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable custom dictionary library at {Path}.", path);
            }
        }

        return libraries;
    }

    // Ensures the new custom library's id collides with neither a built-in id nor an existing file.
    private string UniqueId(string baseSlug)
    {
        var builtinIds = BuiltInDictionaryLibraries.All
            .Select(l => l.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = baseSlug;
        var n = 2;
        while (builtinIds.Contains(candidate) ||
               File.Exists(Path.Combine(_paths.LibrariesDir, candidate + ".csv")))
        {
            candidate = $"{baseSlug}-{n++}";
        }

        return candidate;
    }

    // Lowercase, alphanumerics kept, every other run collapsed to a single hyphen — a safe file name
    // and stable id derived from the library's display name.
    private static string Slugify(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }

                sb.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString();
        return slug.Length == 0 ? "library" : slug;
    }
}
