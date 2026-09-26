using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AzureSignInFieldsTests
{
    [Fact]
    public void An_api_key_stores_only_its_key()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.AzureCli, apiKeySelected: true, "cli-tenant", "sp-tenant", "client", "secret", " key ");
        Assert.Equal(new AzureSignInFields.Stored(null, null, null, "key"), stored);
    }

    [Fact]
    public void Azure_cli_stores_only_its_tenant()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.AzureCli, apiKeySelected: false, " cli-tenant ", "sp-tenant", "client", "secret", "key");
        Assert.Equal(new AzureSignInFields.Stored("cli-tenant", null, null, null), stored);
    }

    [Fact]
    public void A_service_principal_stores_its_own_tenant_and_app_registration()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.ServicePrincipal, apiKeySelected: false, "cli-tenant", "sp-tenant", "client", "secret", "key");
        Assert.Equal(new AzureSignInFields.Stored("sp-tenant", "client", "secret", null), stored);
    }

    [Fact]
    public void Blank_fields_store_nothing()
    {
        var stored = AzureSignInFields.For(AzureAuthMode.ServicePrincipal, apiKeySelected: false, null, "  ", string.Empty, " ", null);
        Assert.Equal(new AzureSignInFields.Stored(null, null, null, null), stored);
    }

    [Fact]
    public void Every_settings_writer_of_the_sign_in_fields_reads_the_shown_projection()
    {
        // Save, the page's draft and Try dictation's draft each build these fields. One that read the boxes itself would
        // disagree with what Save stores whenever a hidden sign-in method's fields still hold text (AI review, round 4).
        var folder = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var writers = Directory.EnumerateFiles(folder, "*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\.AiCleanupAzure(?<field>TenantId|ClientId|ClientSecret|ApiKey)\s*=(?![=>])\s*(?<value>[^;]+);")
                .Select(match => (File: file, Field: match.Groups["field"].Value, Value: match.Groups["value"].Value.Trim())))
            .ToList();

        Assert.True(writers.Count >= 12, $"Only {writers.Count} writers of the sign-in fields were found.");
        Assert.All(writers, writer => Assert.Equal($"signIn.{writer.Field}", writer.Value));
        Assert.All(
            writers.Select(writer => writer.File).Distinct(),
            file => Assert.Contains("var signIn = ShownAzureSignInFields;", File.ReadAllText(file), StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
