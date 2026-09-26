using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class ChoiceControlsTests
{
    [Fact]
    public void History_retention_presets_match_the_plan()
    {
        var set = DurationChoices.Build(DurationChoiceKind.HistoryRetention, 90);

        Assert.Equal(1, set.Minimum);
        Assert.Equal(3650, set.Maximum);
        Assert.Equal(
            ["For 7 days", "For 30 days", "For 90 days (default)", "For 1 year", "Until I delete them", "Custom..."],
            set.Choices.Select(choice => choice.Label));
        Assert.True(set.Choices.Single(choice => choice.Value == 90).IsSelected);
    }

    [Fact]
    public void Max_dictation_presets_include_no_limit_and_custom_range()
    {
        var set = DurationChoices.Build(DurationChoiceKind.MaxDictation, 0);

        Assert.Equal(1, set.Minimum);
        Assert.Equal(1440, set.Maximum);
        Assert.Contains(set.Choices, choice => choice.Value == 0 && choice.Label == "No limit" && choice.IsSelected);
    }

    [Fact]
    public void Idle_release_presets_include_never_and_custom_range()
    {
        var set = DurationChoices.Build(DurationChoiceKind.IdleRelease, 10);

        Assert.Equal(1, set.Minimum);
        Assert.Equal(120, set.Maximum);
        Assert.Contains(set.Choices, choice => choice.Value == 0 && choice.Label == "Never");
        Assert.Contains(set.Choices, choice => choice.Value == 10 && choice.Label == "After 10 minutes (default)" && choice.IsSelected);
    }

    [Fact]
    public void Stored_duration_that_matches_no_preset_selects_custom_without_clamping()
    {
        var set = DurationChoices.Build(DurationChoiceKind.HistoryRetention, 123);

        Assert.True(set.Choices.Single(choice => choice.IsCustom).IsSelected);
        Assert.DoesNotContain(set.Choices, choice => choice.Value == 123);
    }

    [Fact]
    public void Out_of_range_custom_duration_returns_a_validation_issue()
    {
        var issue = DurationChoices.ValidateCustom(DurationChoiceKind.IdleRelease, SettingsPage.Advanced, "IdleReleaseCustomBox", 121);

        Assert.NotNull(issue);
        Assert.Equal(ValidationCode.DurationOutOfRange, issue.Code);
        Assert.Equal("Enter a number from 1 to 120.", issue.Message);
    }

    [Fact]
    public void Thread_choices_are_automatic_then_one_to_sixteen()
    {
        var choices = ThreadChoices.Build(0);

        Assert.Equal("Automatic (recommended)", choices[0].Label);
        Assert.Equal(0, choices[0].Value);
        Assert.True(choices[0].IsSelected);
        Assert.Equal(Enumerable.Range(1, 16), choices.Skip(1).Select(choice => choice.Value));
        Assert.True(ThreadChoices.IsOutOfRange(17));
        Assert.False(ThreadChoices.IsOutOfRange(16));
    }
}
