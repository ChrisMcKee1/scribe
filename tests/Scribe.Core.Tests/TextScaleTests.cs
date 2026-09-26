using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class TextScaleTests
{
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1, 1)]
    [InlineData(1.5, 1.5)]
    [InlineData(3, 2.25)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void NormalizeFactor_clamps_to_the_supported_windows_range(double input, double expected)
    {
        Assert.Equal(expected, TextScale.NormalizeFactor(input));
    }

    [Theory]
    [InlineData(10, 1, 12)]
    [InlineData(12, 1, 12)]
    [InlineData(14, 1, 14)]
    [InlineData(14, 1.5, 21)]
    [InlineData(13, 1.25, 16)]
    [InlineData(26, 2.25, 59)]
    [InlineData(68, 2.25, 153)]
    public void Apply_scales_rounds_and_keeps_text_at_least_12_dips(double baseSize, double factor, double expected)
    {
        Assert.Equal(expected, TextScale.Apply(baseSize, factor));
    }

    [Fact]
    public void Apply_rejects_non_positive_base_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextScale.Apply(0, 1));
    }
}
