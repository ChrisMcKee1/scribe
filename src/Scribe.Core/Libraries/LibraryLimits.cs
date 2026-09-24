namespace Scribe.Core.Libraries;

/// <summary>
/// Resource limits for authoring and importing library terms, and the retention of Recently deleted. Starting targets
/// from the plan; the benchmark (plan 3.14) confirms or replaces them, and only the integrator changes them.
/// </summary>
/// <remarks>
/// Storage is faithful: a managed library file already in the libraries folder loads whatever its size, exactly as
/// 0.4.3 loaded it, so no existing rule stops applying on upgrade. The limits bound what the editor accepts as a new or
/// changed value and what an import reads; an unchanged legacy value past them is kept (review finding R8). They are
/// resource caps, not prompt policy: the glossary's own per-term cap is <c>CleanupPrompt.MaxGlossaryTermChars</c>.
/// </remarks>
public static class LibraryLimits
{
    /// <summary>UTF-16 code units in one new or changed Spoken or Written value, and in one imported field.</summary>
    public const int MaxFieldLength = 2_000;

    /// <summary>Rows one import reads, and one library the editor lets grow to.</summary>
    public const int MaxTermsPerLibrary = 50_000;

    /// <summary>Bytes of one file an import reads.</summary>
    public const long MaxImportBytes = 10L * 1024 * 1024;

    /// <summary>Terms in the enabled libraries past which the Libraries page says dictation may slow down.</summary>
    public const int LargeVocabularyNoticeTerms = 10_000;

    /// <summary>Days a deleted custom library stays in Recently deleted before the janitor removes it.</summary>
    public const int RecentlyDeletedRetentionDays = 30;
}
