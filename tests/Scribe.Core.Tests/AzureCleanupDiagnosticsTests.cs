using System.ClientModel;
using System.ClientModel.Primitives;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Regression coverage for the Azure cleanup failure message. A live outage was misdiagnosed for
/// days because every failure produced the same "check that you're signed in (az login)" text, even
/// though the account was signed in and the real fault was a 403 with no data-plane role assignment,
/// under an auth mode that never calls the Azure CLI at all. The message must name the actual fault.
/// </summary>
public sealed class AzureCleanupDiagnosticsTests
{
    private static Exception Http(int status) => new ClientResultException(new FakeResponse(status));

    /// <summary>Minimal transport response so a real <see cref="ClientResultException"/> carries a status.</summary>
    private sealed class FakeResponse(int status) : PipelineResponse
    {
        public override int Status { get; } = status;
        public override string ReasonPhrase => "test";
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString(string.Empty);
        protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders();
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            new(Content);
        public override void Dispose() { }
    }

    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        public override bool TryGetValue(string name, out string? value) { value = null; return false; }
        public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
    }

    [Fact]
    public void Forbidden_points_at_role_assignment_not_sign_in()
    {
        var message = TextCleanupService.DescribeAzureFailure(
            Http(403), useKey: false, AzureAuthMode.ServicePrincipal, "gpt-5.6-terra");

        Assert.Contains("403", message, StringComparison.Ordinal);
        Assert.Contains("Foundry User", message, StringComparison.Ordinal);
        Assert.DoesNotContain("az login", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forbidden_does_not_recommend_a_cognitive_services_role_for_foundry()
    {
        // Microsoft states it verbatim: "Don't assign built-in roles that start with Cognitive
        // Services... they don't apply to Foundry scenarios." Cognitive Services User currently still
        // works against a Foundry endpoint, which is exactly why this message used to recommend it.
        // Working is not the same as supported, and a 403 is where a user acts on this advice.
        var message = TextCleanupService.DescribeAzureFailure(
            Http(403), useKey: false, AzureAuthMode.ServicePrincipal, "gpt-5.6-terra");

        Assert.DoesNotContain("'Cognitive Services User'", message, StringComparison.Ordinal);

        // The OpenAI-account role is still correct for kind=OpenAI, so it must survive the ban.
        Assert.Contains("Cognitive Services OpenAI User", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forbidden_mentions_propagation_before_blaming_the_role()
    {
        // A freshly assigned role reads as a wrong role for around ten minutes. Sending the user to
        // re-check roles first is what turned a wait into a multi-day misdiagnosis.
        var message = TextCleanupService.DescribeAzureFailure(
            Http(403), useKey: false, AzureAuthMode.ServicePrincipal, "d");

        Assert.Contains("wait", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Not_found_names_the_deployment_rather_than_blaming_credentials()
    {
        var message = TextCleanupService.DescribeAzureFailure(
            Http(404), useKey: false, AzureAuthMode.AzureCli, "gpt-5.6-terra");

        Assert.Contains("gpt-5.6-terra", message, StringComparison.Ordinal);
        Assert.Contains("404", message, StringComparison.Ordinal);
        Assert.DoesNotContain("az login", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_principal_mode_never_tells_the_user_to_run_az_login()
    {
        // ServicePrincipal auth does not shell out to the CLI, so that advice is always wrong here.
        foreach (var ex in new[] { Http(401), Http(403), Http(404), Http(429), Http(500), new HttpRequestException("boom") })
        {
            var message = TextCleanupService.DescribeAzureFailure(
                ex, useKey: false, AzureAuthMode.ServicePrincipal, "d");

            Assert.DoesNotContain("az login", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Azure_cli_mode_still_suggests_signing_in_for_an_unclassified_failure()
    {
        var message = TextCleanupService.DescribeAzureFailure(
            new HttpRequestException("no route to host"), useKey: false, AzureAuthMode.AzureCli, "d");

        Assert.Contains("az login", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Api_key_failures_never_mention_roles_or_tenants()
    {
        var message = TextCleanupService.DescribeAzureFailure(
            new HttpRequestException("boom"), useKey: true, AzureAuthMode.AzureCli, "d");

        Assert.Contains("API key", message, StringComparison.Ordinal);
        Assert.DoesNotContain("az login", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_is_extracted_through_wrapping_exceptions()
    {
        // The Agent Framework wraps client faults, so a naive `is ClientResultException` check would
        // fall through to the generic message and lose the whole diagnosis.
        Assert.Equal(403, TextCleanupService.ExtractHttpStatus(
            new InvalidOperationException("agent run failed", Http(403))));

        Assert.Equal(429, TextCleanupService.ExtractHttpStatus(
            new AggregateException(new Exception("a"), Http(429))));

        Assert.Equal(0, TextCleanupService.ExtractHttpStatus(new InvalidOperationException("plain")));
    }
}
