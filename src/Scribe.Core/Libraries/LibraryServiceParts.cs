namespace Scribe.Core.Libraries;

/// <summary>
/// What the library service is built from: the three pure parts other sub-streams implement (the CSV codec, the built-in
/// overlay, composition and policy), the file system seam the journal's fault suite replaces, the clock (pattern P-10),
/// the session context, and the manifest id source. Tests inject fakes of any of them through the service's internal
/// constructor; the public constructor uses <see cref="Default"/>.
/// </summary>
/// <param name="Codec">How library files and exchanged CSVs are read and written.</param>
/// <param name="Overlay">How a built-in's edits document applies.</param>
/// <param name="Composer">The local state's persistence, adoption, composition and the revocation check.</param>
/// <param name="Files">Every file operation of the library storage.</param>
/// <param name="Time">The clock for journal and Recently deleted stamps.</param>
/// <param name="Context">
/// What the session knows beyond the library service's own reads: a session on defaults and a repair at this start.
/// The service adds what it reads itself (an unreadable or lost stored document, a stored generation, the witness file).
/// </param>
/// <param name="NewManifestId">A new manifest id: <see cref="Guid.NewGuid"/> in the app, a scripted sequence in tests.</param>
internal sealed record LibraryServiceParts(
    ILibraryCsvCodec Codec,
    IBuiltInLibraryOverlay Overlay,
    ILibraryComposer Composer,
    ILibraryFileSystem Files,
    TimeProvider Time,
    Func<LibraryStateContext> Context,
    Func<Guid> NewManifestId)
{
    /// <summary>The interim adapters in this branch (<c>InterimLibraryParts.cs</c>); the integration commit swaps in X, O and C.</summary>
    internal static LibraryServiceParts Default { get; } = new(
        InterimCsvCodec.Instance,
        InterimOverlay.Instance,
        InterimComposer.Instance,
        PhysicalLibraryFileSystem.Instance,
        TimeProvider.System,
        static () => default,
        Guid.NewGuid);

    /// <summary>
    /// The built-ins that no longer ship (<see cref="LibraryPrecedence.RetiredBuiltInIds"/>, release 0.4.4's list, empty so
    /// far), whose edits documents are listed as <see cref="RetiredBuiltInEdits"/>; a test names ids of its own here.
    /// </summary>
    internal IReadOnlyList<string> RetiredBuiltInIds { get; init; } = LibraryPrecedence.RetiredBuiltInIds;
}
