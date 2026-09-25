namespace Scribe.Core.Libraries;

/// <summary>
/// The names one library has, which differ only for a hand-placed file remapped away from a built-in id: its logical
/// <see cref="Id"/>, which the local state refers to; its physical <see cref="FileName"/>, which decides its precedence
/// among custom libraries; and <see cref="LegacyId"/>, the id 0.4.3 and 0.4.2 load it as.
/// </summary>
/// <remarks>
/// <para>
/// A hand-placed <c>github.csv</c> keeps its file in place (no physical rename, which would also move it among older
/// builds' custom libraries) and gets the logical id <c>custom-github</c>, so the built-in GitHub and it have separate
/// enabled states and AI permissions in this version. It still ranks by <c>github.csv</c>, exactly where 0.4.3 ranked
/// it (review finding A16: with <c>epsilon.csv</c> beside it, 0.4.3 lets epsilon win a shared spoken form, and ranking
/// by <c>custom-github.csv</c> would hand it to the twin). And an older build still loads it as <c>github</c>, together
/// with the built-in, under one enabled flag, which is why the state encoder writes a shared legacy id into the settings
/// document's list only when every library it stands for is on and permitted for AI (A15), before the commit's physical
/// changes as well as after them (A18), and why reading that list back applies an older build's change of the shared id
/// to every library it stands for (A17).
/// </para>
/// <para>
/// Invariants the producer keeps (J for committed libraries, D for draft ones): a built-in has no file name; a custom
/// library has the file name it is stored under, or for one not written yet the name it will be written as,
/// <c>Id + ".csv"</c>.
/// </para>
/// </remarks>
/// <param name="Id">The logical id, compared case-insensitively.</param>
/// <param name="BuiltIn">Whether the library ships with Scribe.</param>
/// <param name="FileName">A custom library's file name in the libraries folder; null for a built-in.</param>
public sealed record LibraryIdentity(string Id, bool BuiltIn, string? FileName)
{
    /// <summary>The id 0.4.3 and 0.4.2 load the library as: a built-in's id, or a custom file's name without <c>.csv</c>.</summary>
    public string LegacyId =>
        BuiltIn || string.IsNullOrEmpty(FileName) ? Id : Path.GetFileNameWithoutExtension(FileName);

    /// <summary>The name a custom library ranks by among custom libraries (<see cref="LibraryPrecedence"/>); null for a built-in.</summary>
    public string? PrecedenceName => BuiltIn ? null : FileName ?? Id + ".csv";

    /// <summary>A custom library not written yet, which will be stored as <c>Id + ".csv"</c>.</summary>
    public static LibraryIdentity NewCustom(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new LibraryIdentity(id, BuiltIn: false, id + ".csv");
    }
}
