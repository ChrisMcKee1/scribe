using Scribe.Core.Models;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The built-in profile templates. These guard the properties that make a preset actually work:
/// every process name must survive the same normalisation the matcher applies, and the presets
/// must stay templates rather than becoming defaults that change behaviour on upgrade.
/// </summary>
public sealed class ProfilePresetsTests
{
    [Fact]
    public void Every_preset_has_a_name_a_description_and_processes()
    {
        Assert.NotEmpty(ProfilePresets.All);

        foreach (var preset in ProfilePresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Profile.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.NotEmpty(preset.Profile.ProcessNames);
        }
    }

    [Fact]
    public void Preset_names_are_unique()
    {
        var names = ProfilePresets.All.Select(p => p.Profile.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// A process name that is blank, or that round-trips badly through the editor's
    /// comma-separated text box, would silently never match.
    /// </summary>
    [Fact]
    public void Process_names_are_trimmed_non_empty_and_comma_free()
    {
        foreach (var preset in ProfilePresets.All)
        {
            foreach (var process in preset.Profile.ProcessNames)
            {
                Assert.False(string.IsNullOrWhiteSpace(process));
                Assert.Equal(process.Trim(), process);
                Assert.DoesNotContain(',', process);
            }
        }
    }

    [Fact]
    public void Process_names_are_unique_within_a_preset()
    {
        foreach (var preset in ProfilePresets.All)
        {
            var processes = preset.Profile.ProcessNames;

            Assert.Equal(
                processes.Count,
                processes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    /// <summary>
    /// Two presets claiming the same process would make the result depend on list order, since
    /// the matcher takes the first hit.
    /// </summary>
    [Fact]
    public void No_process_appears_in_two_presets()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in ProfilePresets.All)
        {
            foreach (var process in preset.Profile.ProcessNames)
            {
                Assert.False(
                    seen.TryGetValue(process, out var owner),
                    $"'{process}' is claimed by both '{owner}' and '{preset.Profile.Name}'.");
                seen[process] = preset.Profile.Name;
            }
        }
    }

    /// <summary>
    /// The presets exist to be edited. Handing out the shared static instance would let the
    /// settings editor mutate the template for every later use in the same session.
    /// </summary>
    [Fact]
    public void Instantiate_returns_an_independent_copy()
    {
        var preset = ProfilePresets.TerminalsAndShells;

        var first = ProfilePresets.Instantiate(preset);
        first.ProcessNames.Add("something-else");
        first.Name = "renamed";

        var second = ProfilePresets.Instantiate(preset);

        Assert.Equal(preset.Profile.Name, second.Name);
        Assert.DoesNotContain("something-else", second.ProcessNames);
        Assert.Equal(preset.Profile.ProcessNames.Count, second.ProcessNames.Count);
    }

    [Fact]
    public void Terminals_preset_flattens_and_covers_shells()
    {
        var profile = ProfilePresets.Instantiate(ProfilePresets.TerminalsAndShells);

        Assert.Equal(NewlineInjectionMode.AlwaysFlatten, profile.NewlineHandling);
        Assert.Contains("WindowsTerminal", profile.ProcessNames);
        Assert.Contains("pwsh", profile.ProcessNames);
    }

    /// <summary>
    /// A process name cannot tell an editor pane from an integrated terminal, so editors are a
    /// separate, explicitly warned preset rather than being bundled with shells. They also carry
    /// no writing style: an editor is exactly where markdown and code fences are wanted.
    /// </summary>
    [Fact]
    public void Editors_are_separate_from_terminals_and_carry_no_writing_style()
    {
        var terminals = ProfilePresets.Instantiate(ProfilePresets.TerminalsAndShells);
        var ides = ProfilePresets.Instantiate(ProfilePresets.IdeIntegratedTerminals);

        Assert.DoesNotContain("Code", terminals.ProcessNames);
        Assert.DoesNotContain("devenv", terminals.ProcessNames);
        Assert.Contains("Code", ides.ProcessNames);
        Assert.Contains("devenv", ides.ProcessNames);

        Assert.False(string.IsNullOrWhiteSpace(terminals.WritingStyle));
        Assert.Null(ides.WritingStyle);
    }

    /// <summary>
    /// The editor preset changes behaviour in source files, so the menu description has to say so
    /// rather than leaving the user to discover it.
    /// </summary>
    [Fact]
    public void Editor_preset_description_warns_about_the_editor_pane()
    {
        Assert.Contains(
            "Warning",
            ProfilePresets.IdeIntegratedTerminals.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reported failure: AI cleanup adds paragraph breaks that raw recognition never contains,
    /// and each one submitted a partial message in the GitHub Copilot desktop app.
    /// </summary>
    [Fact]
    public void Ai_chat_preset_flattens_and_covers_the_reported_app()
    {
        var profile = ProfilePresets.Instantiate(ProfilePresets.AiChatAndAgents);

        Assert.Equal(NewlineInjectionMode.AlwaysFlatten, profile.NewlineHandling);
        Assert.Contains("github", profile.ProcessNames);
        Assert.Contains("claude", profile.ProcessNames);
        Assert.Contains("ChatGPT", profile.ProcessNames);
    }

    /// <summary>
    /// Teams is the highest-volume target in real usage and normally honours Shift+Enter, so it
    /// must stay a deliberate opt-in rather than being folded into the chat preset.
    /// </summary>
    [Fact]
    public void Teams_is_a_separate_preset_from_ai_chat()
    {
        var chat = ProfilePresets.Instantiate(ProfilePresets.AiChatAndAgents);
        var teams = ProfilePresets.Instantiate(ProfilePresets.Teams);

        Assert.DoesNotContain("ms-teams", chat.ProcessNames);
        Assert.Contains("ms-teams", teams.ProcessNames);
    }

    [Fact]
    public void Documents_preset_keeps_newlines()
    {
        var profile = ProfilePresets.Instantiate(ProfilePresets.Documents);

        Assert.Equal(NewlineInjectionMode.KeepNewlines, profile.NewlineHandling);
        Assert.Contains("WINWORD", profile.ProcessNames);
        Assert.Contains("EXCEL", profile.ProcessNames);
    }

    /// <summary>
    /// End to end: a preset added in the editor must match the process it names once persisted.
    /// </summary>
    [Theory]
    [InlineData("github")]
    [InlineData("GITHUB.EXE")]
    [InlineData("claude")]
    [InlineData(" ChatGPT ")]
    public void Ai_chat_preset_matches_its_processes_through_the_matcher(string focused)
    {
        var profiles = ProfileBuilder.Build(
        [
            new ProfileBuilder.Row(
                ProfilePresets.AiChatAndAgents.Profile.Name,
                string.Join(", ", ProfilePresets.AiChatAndAgents.Profile.ProcessNames),
                ProfilePresets.AiChatAndAgents.Profile.WritingStyle,
                ProfilePresets.AiChatAndAgents.Profile.NewlineHandling),
        ]);

        var match = AppProfileMatcher.Match(profiles, focused);

        Assert.NotNull(match);
        Assert.Equal(NewlineInjectionMode.AlwaysFlatten, match!.NewlineHandling);
    }
}
