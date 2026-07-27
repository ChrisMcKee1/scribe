using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Entra's own wording for a not-yet-propagated secret is actively misleading, so these pin the
/// translations users actually depend on.
/// </summary>
public class AzureSignInDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some unrelated transport failure")]
    public void An_unrecognized_failure_falls_back_to_the_generic_message(string? text) =>
        Assert.Equal(AzureSignInDiagnostics.Generic, AzureSignInDiagnostics.Describe(text));

    [Fact]
    public void A_rejected_secret_mentions_propagation_and_the_value_versus_id_mistake()
    {
        var message = AzureSignInDiagnostics.Describe(
            "AADSTS7000215: Invalid client secret provided. Ensure the secret being sent in the "
            + "request is the client secret value, not the client secret ID");

        Assert.NotEqual(AzureSignInDiagnostics.Generic, message);
        Assert.Contains("moment", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Value", message, StringComparison.Ordinal);
        Assert.Contains("Secret ID", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_tenant_is_called_out_by_name()
    {
        var message = AzureSignInDiagnostics.Describe(
            "AADSTS700016: Application with identifier 'x' was not found in the directory");

        Assert.Contains("tenant", message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AzureSignInDiagnostics.Generic, message);
    }

    [Fact]
    public void An_expired_secret_says_to_create_a_new_one()
    {
        var message = AzureSignInDiagnostics.Describe("AADSTS7000222: The provided client secret keys are expired");

        Assert.Contains("expired", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new one", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_service_principal_is_distinguished_from_a_bad_secret()
    {
        var message = AzureSignInDiagnostics.Describe(
            "AADSTS700213: No service principal found in the tenant");

        Assert.Contains("service principal", message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AzureSignInDiagnostics.Generic, message);
    }

    [Fact]
    public void Matching_is_case_insensitive() =>
        Assert.Equal(
            AzureSignInDiagnostics.Describe("aadsts7000215: invalid client secret"),
            AzureSignInDiagnostics.Describe("AADSTS7000215: Invalid client secret"));

    [Fact]
    public void Every_translation_is_a_complete_sentence()
    {
        string[] codes =
        [
            "AADSTS7000215", "AADSTS700016", "AADSTS7000222", "AADSTS7000216", "AADSTS700213",
        ];

        foreach (var code in codes)
        {
            var message = AzureSignInDiagnostics.Describe($"{code}: something went wrong");
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.EndsWith(".", message.Trim(), StringComparison.Ordinal);
        }
    }
}
