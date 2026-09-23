using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the two patterns <see cref="CleanupFailureShape"/> matches, the service error code and the Entra
/// code, so the move from runtime-compiled to source-generated regexes cannot have changed what they accept.
/// </summary>
public sealed class CleanupFailureShapePatternTests
{
    [Theory]
    [InlineData("a", "a")]
    [InlineData("Z9_-", "Z9_-")]
    [InlineData("invalid_request_error", "invalid_request_error")]
    [InlineData("  DeploymentNotFound  ", "DeploymentNotFound")] // trimmed first
    [InlineData("RateLimited\n", "RateLimited")]
    [InlineData("9abc", null)] // must open with a letter
    [InlineData("_abc", null)]
    [InlineData("-abc", null)]
    [InlineData("abc.def", null)]
    [InlineData("abc:def", null)]
    [InlineData("abc/def", null)]
    [InlineData("abc def", null)]
    [InlineData("caf\u00e9", null)] // ASCII letters only
    [InlineData("\u00c9t\u00e9", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_service_code_is_an_ascii_identifier(string? value, string? expected)
    {
        Assert.Equal(expected, CleanupFailureShape.SanitizeCode(value));
    }

    [Fact]
    public void A_service_code_is_at_most_64_characters()
    {
        var longest = "a" + new string('b', 63);

        Assert.Equal(longest, CleanupFailureShape.SanitizeCode(longest));
        Assert.Null(CleanupFailureShape.SanitizeCode(longest + "c"));
    }

    [Theory]
    [InlineData("AADSTS700016: Application with identifier 'x' was not found.", "AADSTS700016")]
    [InlineData("failed (AADSTS7000215).", "AADSTS7000215")]
    [InlineData("AADSTS1234 is the shortest", "AADSTS1234")]
    [InlineData("AADSTS123456789 is the longest", "AADSTS123456789")]
    [InlineData("AADSTS\u0667\u0660\u0660\u0660\u0661\u0666 uses other digits", "AADSTS\u0667\u0660\u0660\u0660\u0661\u0666")] // \d is Unicode
    [InlineData("AADSTS123 is too short", null)]
    [InlineData("AADSTS1234567890 is too long", null)]
    [InlineData("xAADSTS700016 is inside a word", null)]
    [InlineData("AADSTS700016x is inside a word", null)]
    [InlineData("aadsts700016 is the wrong case", null)]
    public void An_entra_code_is_a_whole_word_with_four_to_nine_digits(string message, string? expected)
    {
        var shape = CleanupFailureShape.Describe(new InvalidOperationException(message));

        Assert.Equal(expected is null ? "InvalidOperationException" : $"InvalidOperationException aadsts={expected}", shape);
    }

    [Fact]
    public void Entra_codes_are_collected_once_each_outermost_first_and_at_most_three()
    {
        var failure = new InvalidOperationException(
            "AADSTS1111 then AADSTS2222 then AADSTS1111 again",
            new ArgumentException("AADSTS3333 and AADSTS4444"));

        var shape = CleanupFailureShape.Describe(failure);

        Assert.Equal("InvalidOperationException aadsts=AADSTS1111,AADSTS2222,AADSTS3333 inner=ArgumentException", shape);
    }
}
