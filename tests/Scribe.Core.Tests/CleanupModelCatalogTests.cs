using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the curated catalog metadata that the settings UI surfaces. The recommendation strings
/// are pinned to the golden-suite benchmark winners in docs/model-leaderboard.md, so a stray edit
/// that mislabels a model (or drops a winner from the list) fails here rather than in the UI.
/// </summary>
public sealed class CleanupModelCatalogTests
{
    [Fact]
    public void Default_alias_is_present_in_the_curated_list()
    {
        Assert.Contains(CleanupModelCatalog.Curated,
            m => string.Equals(m.Alias, CleanupModelCatalog.DefaultAlias, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_the_leaderboard_winners_carry_a_recommendation()
    {
        var recommended = CleanupModelCatalog.Curated
            .Where(m => m.Recommendation is not null)
            .Select(m => m.Alias)
            .ToArray();

        Assert.Equal(new[] { "mistral-nemo-12b-instruct", "phi-4" }, recommended);
    }

    [Theory]
    [InlineData("mistral-nemo-12b-instruct", "Best on-device balance")]
    [InlineData("phi-4", "Best on-device quality")]
    public void Winners_carry_the_expected_recommendation_text(string alias, string recommendation)
    {
        var model = CleanupModelCatalog.Curated.Single(m => m.Alias == alias);
        Assert.Equal(recommendation, model.Recommendation);
    }

    [Fact]
    public void Non_winners_leave_recommendation_null()
    {
        foreach (var model in CleanupModelCatalog.Curated)
        {
            if (model.Alias is "mistral-nemo-12b-instruct" or "phi-4")
            {
                continue;
            }

            Assert.Null(model.Recommendation);
        }
    }

    [Fact]
    public void Resolve_returns_the_curated_descriptor_with_its_recommendation()
    {
        var model = CleanupModelCatalog.Resolve("phi-4");

        Assert.Equal("phi-4", model.Alias);
        Assert.Equal("Best on-device quality", model.Recommendation);
    }

    [Fact]
    public void Resolve_of_an_unknown_alias_has_no_recommendation()
    {
        var model = CleanupModelCatalog.Resolve("some-uncurated-model");

        Assert.Equal("some-uncurated-model", model.Alias);
        Assert.Null(model.Recommendation);
    }
}
