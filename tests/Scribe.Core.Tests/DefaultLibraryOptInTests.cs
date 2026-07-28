using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the first-run library opt-in. Enabling a library changes how someone's dictation is
/// rewritten, so the rules are narrow: a brand new install gets the AI vocabulary, and nothing else
/// ever gains a library it did not ask for.
/// </summary>
public sealed class DefaultLibraryOptInTests
{
    [Fact]
    public void Fresh_install_enables_the_ai_libraries()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var loaded = repo.Load();

        Assert.Contains("ai-model-names", loaded.EnabledDictionaryLibraryIds);
        Assert.Contains("ai-terminology", loaded.EnabledDictionaryLibraryIds);
    }

    [Fact]
    public void Fresh_install_does_not_enable_the_platform_specific_libraries()
    {
        // Those are opinionated (Azure, GitHub, .NET). Turning them on for everyone would rewrite
        // words for users who never work in those stacks.
        var defaults = AppSettings.CreateDefault().EnabledDictionaryLibraryIds;

        Assert.DoesNotContain("microsoft-azure", defaults);
        Assert.DoesNotContain("github", defaults);
        Assert.DoesNotContain("dotnet-development", defaults);
    }

    [Fact]
    public void Default_library_ids_all_exist_as_shipped_libraries()
    {
        // A typo here would silently ship a default that enables nothing.
        var known = BuiltInDictionaryLibraries.All.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in AppSettings.DefaultLibraryIds)
        {
            Assert.Contains(id, known);
        }
    }

    [Fact]
    public void Existing_install_that_turned_everything_off_stays_off()
    {
        // An empty saved list is a deliberate choice, not a missing value. Re-seeding it on upgrade
        // would change someone's output with no action on their part.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Clear();
        repo.Save(settings);

        Assert.Empty(repo.Load().EnabledDictionaryLibraryIds);
    }

    [Fact]
    public void Existing_install_predating_the_setting_is_not_opted_in()
    {
        // Settings JSON written before the field existed has no key at all. Deserialization must not
        // fall through to a property initializer carrying the first-run defaults.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", """{"hotkey":null,"enableAiCleanup":true}""");

        Assert.Empty(repo.Load().EnabledDictionaryLibraryIds);
    }

    [Fact]
    public void Existing_install_keeps_its_own_selection_untouched()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var settings = AppSettings.CreateDefault();
        settings.EnabledDictionaryLibraryIds.Clear();
        settings.EnabledDictionaryLibraryIds.Add("github");
        repo.Save(settings);

        Assert.Equal(["github"], repo.Load().EnabledDictionaryLibraryIds);
    }
}
