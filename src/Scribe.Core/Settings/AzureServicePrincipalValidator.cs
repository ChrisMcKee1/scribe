namespace Scribe.Core.Settings;

/// <summary>
/// How Scribe obtains an Entra ID token for Microsoft Foundry cleanup.
/// </summary>
public enum AzureAuthMode
{
    /// <summary>The signed-in user's Azure CLI account (<c>az login</c>). The default.</summary>
    AzureCli = 0,

    /// <summary>
    /// An Entra ID app registration (tenant id, client id, client secret). Lets a user who belongs
    /// to several tenants pin one identity instead of depending on whichever account the CLI
    /// happens to have active.
    /// </summary>
    ServicePrincipal = 1,
}

/// <summary>
/// Pure validation for the service principal credentials entered in Settings. Kept out of the WPF
/// window so the rules are testable, and so a half-filled form can never reach the credential
/// factory: a service principal that is missing a field fails at token request time with an opaque
/// Entra error, which is a miserable thing to debug from a dictation app.
/// </summary>
public static class AzureServicePrincipalValidator
{
    public enum Issue
    {
        None,
        TenantIdRequired,
        TenantIdMalformed,
        ClientIdRequired,
        ClientIdMalformed,
        ClientSecretRequired,
    }

    /// <summary>True when all three fields are present and well formed.</summary>
    public static bool IsComplete(string? tenantId, string? clientId, string? clientSecret) =>
        Validate(tenantId, clientId, clientSecret) == Issue.None;

    public static Issue Validate(string? tenantId, string? clientId, string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Issue.TenantIdRequired;
        }

        // A tenant may be given as a GUID or as a verified domain (contoso.onmicrosoft.com); Entra
        // accepts both, so rejecting anything that is not a GUID would turn away a valid value.
        if (!LooksLikeTenant(tenantId))
        {
            return Issue.TenantIdMalformed;
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Issue.ClientIdRequired;
        }

        // The application (client) id is always a GUID, so this one can be strict.
        if (!Guid.TryParse(clientId.Trim(), out _))
        {
            return Issue.ClientIdMalformed;
        }

        return string.IsNullOrWhiteSpace(clientSecret) ? Issue.ClientSecretRequired : Issue.None;
    }

    /// <summary>A human-readable explanation for <paramref name="issue"/>, or null when valid.</summary>
    public static string? Describe(Issue issue) => issue switch
    {
        Issue.None => null,
        Issue.TenantIdRequired => "Enter the directory (tenant) ID for the service principal.",
        Issue.TenantIdMalformed =>
            "The tenant must be a GUID or a domain such as contoso.onmicrosoft.com.",
        Issue.ClientIdRequired => "Enter the application (client) ID for the service principal.",
        Issue.ClientIdMalformed => "The application (client) ID must be a GUID.",
        Issue.ClientSecretRequired => "Enter the client secret for the service principal.",
        _ => "Complete the service principal details.",
    };

    private static bool LooksLikeTenant(string value)
    {
        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out _))
        {
            return true;
        }

        // A domain form: at least one dot, no whitespace, and no scheme or path characters.
        if (trimmed.IndexOf('.') <= 0 || trimmed.EndsWith('.'))
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch) || ch is '/' or '\\' or ':' or '@')
            {
                return false;
            }
        }

        return true;
    }
}
