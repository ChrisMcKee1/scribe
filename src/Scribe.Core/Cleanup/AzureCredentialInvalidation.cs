namespace Scribe.Core.Cleanup;

/// <summary>
/// Drops the cached Microsoft Foundry credential. Azure.Identity caches tokens per credential
/// instance and Microsoft warns that not reusing instances invites HTTP 429 throttling, so Scribe
/// holds one. That cache has to be dropped whenever the identity changes, or a corrected secret or
/// a fresh <c>az login</c> would keep serving the old credential.
/// </summary>
public static class AzureCredentialInvalidation
{
    public static void Invalidate() => AzureCredentialFactory.Invalidate();
}
