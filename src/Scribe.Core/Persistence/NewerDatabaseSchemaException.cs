namespace Scribe.Core.Persistence;

/// <summary>
/// The database was written by a newer Scribe: its <c>user_version</c> is above the schema this build
/// knows. Nothing was changed. The app shows <see cref="UserMessage"/> and exits, because running on
/// against a schema it does not understand could damage the newer build's data.
/// </summary>
public sealed class NewerDatabaseSchemaException : InvalidOperationException
{
    /// <summary>What the user is told before the app exits.</summary>
    public const string UserMessage =
        "This data was created by a newer version of Scribe. Please install the latest version.";

    public NewerDatabaseSchemaException(int databaseVersion, int supportedVersion)
        : base(
            $"This database uses schema v{databaseVersion}, but this Scribe build supports only v{supportedVersion}. " +
            "Install a newer Scribe version instead of downgrading.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>The schema version the database file reports.</summary>
    public int DatabaseVersion { get; }

    /// <summary>The newest schema version this build can open.</summary>
    public int SupportedVersion { get; }
}
