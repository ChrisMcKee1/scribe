namespace Scribe.Core.Libraries;

/// <summary>
/// Resource limits for authoring and importing library terms, and the retention of Recently deleted. Set at the W1b
/// integration from the library benchmarks (plan 3.14, <c>tools\Scribe.Benchmarks</c>), which confirmed the plan's starting
/// targets; only the integrator changes them.
/// </summary>
/// <remarks>
/// <para>
/// Storage is faithful: a managed library file already in the libraries folder loads whatever its size, exactly as
/// 0.4.3 loaded it, so no existing rule stops applying on upgrade. The limits bound what the editor accepts as a new or
/// changed value and what an import reads; an unchanged legacy value past them is kept (review finding R8). They are
/// resource caps, not prompt policy: the glossary's own per-term cap is <c>CleanupPrompt.MaxGlossaryTermChars</c>.
/// </para>
/// <para>
/// The measurements behind them (one custom library of N terms beside the built-ins, x64 desktop, BenchmarkDotNet ShortRun
/// at High priority, so upper-bound machines rather than a user's): one dictation's post-processing took 0.2 ms at 1,549
/// terms, 3.7 ms at 10,000 and 61 ms at 100,000; from a Save to the next dictation being ready, 42 ms, 0.17 s and 1.4 s;
/// the rules alone, rebuilt at every start and Save, 6 ms, 71 ms and 0.79 s. Below 10,000 terms none of it is noticeable
/// beside speech recognition; past it every cost grows faster than the vocabulary, which is where the notice goes. One
/// library at the row cap costs, by interpolation, about 0.6 s from its Save to the next dictation and about 30 ms a
/// dictation. Not all of it is off the dispatcher yet: until the vocabulary publication stream (W-V) lands,
/// <c>DictationController.Start</c> and every Settings Save's settings application load the catalog and compile the rules
/// synchronously on it, through release 0.4.4's library seam. Staging a Save read, hashed and wrote a 100,000-term file
/// (about 3 MB) in 90 ms, so an import at the byte cap stays within a few hundred milliseconds. The field cap was not
/// measured separately; it bounds one rule's pattern and the Term details editor, and stays far above the glossary's own
/// 100.
/// </para>
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

    /// <summary>
    /// Days a set-aside journal manifest and its own journal files (its redo images, install copies and spare backups)
    /// stay quarantined before the janitor removes them: the retention damaged database copies get
    /// (<see cref="Persistence.StorageRetentionPolicy.DamagedCopyRetentionDays"/>), because both hold what a failure
    /// left behind and may be the only copy of it (review finding G5). Library files the manifest names (a target, a
    /// kept outside version, a set-aside edits document, a Recently deleted entry) are never removed by the quarantine:
    /// they follow their own rules (review finding G11).
    /// </summary>
    public const int QuarantineRetentionDays = Persistence.StorageRetentionPolicy.DamagedCopyRetentionDays;
}
