namespace Scribe.Core.Cleanup;

/// <summary>
/// Turns an Entra sign-in failure into something a user can act on.
/// </summary>
/// <remarks>
/// Entra's own wording is misleading in the case that matters most here: a client secret that was
/// created seconds ago is rejected with AADSTS7000215 "Invalid client secret provided", which reads
/// as "you typed it wrong" when the real answer is "wait a moment and try again". Observed
/// directly: the same secret failed and then succeeded roughly thirty seconds later.
/// </remarks>
public static class AzureSignInDiagnostics
{
    /// <summary>The message shown when nothing more specific can be determined.</summary>
    public const string Generic =
        "The service principal could not sign in. Check the tenant, client ID, and secret, and that the secret has not expired.";

    /// <summary>
    /// Maps the text of an authentication failure to a specific explanation, falling back to
    /// <see cref="Generic"/>.
    /// </summary>
    public static string Describe(string? failureText)
    {
        if (string.IsNullOrWhiteSpace(failureText))
        {
            return Generic;
        }

        if (Contains(failureText, "AADSTS7000215"))
        {
            return "The client secret was rejected. A secret created in the last minute or two may "
                + "just need a moment to become active, so wait and try again. Otherwise make sure "
                + "you copied the secret Value and not the Secret ID.";
        }

        if (Contains(failureText, "AADSTS700016") || Contains(failureText, "AADSTS90002"))
        {
            return "That application was not found in this tenant. Check the directory (tenant) ID "
                + "and the application (client) ID, and that the app registration lives in that directory.";
        }

        if (Contains(failureText, "AADSTS7000222") || Contains(failureText, "expired"))
        {
            return "The client secret has expired. Create a new one in the Azure portal and enter its Value here.";
        }

        if (Contains(failureText, "AADSTS7000216") || Contains(failureText, "AADSTS900023"))
        {
            return "The tenant or client ID looks malformed. Both come from the app registration overview page.";
        }

        if (Contains(failureText, "AADSTS50034") || Contains(failureText, "AADSTS700213"))
        {
            return "No service principal exists for that application in this tenant. The app registration "
                + "may have been created in a different directory.";
        }

        return Generic;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
