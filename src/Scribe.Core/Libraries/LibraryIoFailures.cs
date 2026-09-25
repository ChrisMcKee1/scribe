namespace Scribe.Core.Libraries;

/// <summary>
/// Turns a file-system exception into the shape the journal decides by and the shell words: a
/// <see cref="LibraryIoFailure"/>, and the few Win32 codes whose meaning changes what the journal does next. Every
/// check reads the HRESULT, never a message, so it holds in every language Windows speaks.
/// </summary>
internal static class LibraryIoFailures
{
    // HRESULT_FROM_WIN32 of the Win32 errors the journal distinguishes.
    internal const int FileNotFound = unchecked((int)0x80070002);
    internal const int PathNotFound = unchecked((int)0x80070003);
    internal const int AccessDenied = unchecked((int)0x80070005);
    internal const int SharingViolation = unchecked((int)0x80070020);
    internal const int LockViolation = unchecked((int)0x80070021);
    internal const int HandleDiskFull = unchecked((int)0x80070027);
    internal const int FileExists = unchecked((int)0x80070050);
    internal const int DiskFull = unchecked((int)0x80070070);
    internal const int AlreadyExists = unchecked((int)0x800700B7);
    internal const int FilenameExceedsRange = unchecked((int)0x800700CE);

    /// <summary>ERROR_UNABLE_TO_REMOVE_REPLACED (1175): the files keep their names.</summary>
    internal const int UnableToRemoveReplaced = unchecked((int)0x80070497);

    /// <summary>ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176): the files keep their names.</summary>
    internal const int UnableToMoveReplacement = unchecked((int)0x80070498);

    /// <summary>
    /// ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177): the target was renamed to the backup and the replacement is still
    /// under its own name (case W5 of the journal's resume table).
    /// </summary>
    internal const int UnableToMoveReplacement2 = unchecked((int)0x80070499);

    public static LibraryIoFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            UnauthorizedAccessException => LibraryIoFailure.AccessDenied,
            PathTooLongException => LibraryIoFailure.PathTooLong,
            _ => exception.HResult switch
            {
                SharingViolation or LockViolation => LibraryIoFailure.SharingViolation,
                AccessDenied => LibraryIoFailure.AccessDenied,
                DiskFull or HandleDiskFull => LibraryIoFailure.DiskFull,
                FilenameExceedsRange => LibraryIoFailure.PathTooLong,
                _ => LibraryIoFailure.Other,
            },
        };
    }

    /// <summary>Another app holds the file open without the sharing the operation needs.</summary>
    public static bool IsSharingViolation(Exception exception) =>
        exception.HResult is SharingViolation or LockViolation;

    /// <summary>A move or a create without overwrite found its destination taken.</summary>
    public static bool IsAlreadyExists(Exception exception) =>
        exception.HResult is FileExists or AlreadyExists;

    /// <summary>The file or a folder on its path is not there.</summary>
    public static bool IsNotFound(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException ||
        exception.HResult is FileNotFound or PathNotFound;

    /// <summary>The native replace renamed the target to its backup and left the replacement in place (1177).</summary>
    public static bool IsReplacementLeftBehind(Exception exception) => exception.HResult == UnableToMoveReplacement2;
}
