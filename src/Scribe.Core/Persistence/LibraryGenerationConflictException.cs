namespace Scribe.Core.Persistence;

/// <summary>
/// A library commit found the stored library generation (<see cref="Libraries.LibrarySettingKeys.Generation"/>) at a
/// value other than the one it was prepared against, so something else committed library state since. Nothing was
/// written; the caller completes or discards its preparation by the stored generation and reloads.
/// </summary>
public sealed class LibraryGenerationConflictException : InvalidOperationException
{
    public LibraryGenerationConflictException(long stored, long expected)
        : base("The libraries were changed by another save, so this one was not committed.")
    {
        Stored = stored;
        Expected = expected;
    }

    /// <summary>The generation the settings store holds; 0 when the row is absent or unreadable.</summary>
    public long Stored { get; }

    /// <summary>The generation the commit was prepared against.</summary>
    public long Expected { get; }
}
