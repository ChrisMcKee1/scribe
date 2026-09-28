namespace Scribe.Core.Diagnostics;

/// <summary>
/// The performance changes that ship switched off, each turned on by name through the <c>SCRIBE_PERF_FLAGS</c>
/// environment variable, read once at startup: names separated by commas, semicolons or white space, compared without
/// regard to case. Off is the old path, so a process without the variable runs exactly what the release before ran; a
/// flag's default flips only in a later release, once its field evidence is in.
/// </summary>
/// <remarks>
/// An environment variable, not a setting or an AppContext switch, because it reaches both installs the same way (the
/// Store package's runtimeconfig cannot be edited) and never enters the settings document. The session banner lists the
/// names that are on and counts the unknown ones; an unknown name is never echoed, since the value is free text.
/// </remarks>
public sealed class PerfFlags
{
    /// <summary>The environment variable the flags are read from.</summary>
    public const string EnvironmentVariable = "SCRIBE_PERF_FLAGS";

    // Every flag this build knows, in the order the banner lists them: alphabetical, one per line, each with its constant
    // below. A name here with no code behind it would claim a change that does not run, so PerfFlagsTests fails for a
    // known name that nothing in src reads.
    private static readonly string[] KnownNames =
    [
        AggressiveIdleGc,
        AsyncDeviceList,
        BatchCleanupSelectionCounts,
        BoundedDiagnosticsReads,
        BoundedUsageTokenLookup,
        CachedRowSearchText,
        CacheWordPackPreview,
        CacheWordPackSort,
        CaptureTimingDiagnostics,
        CleanupPhaseTelemetry,
        CliAccessTokenCache,
        CoalesceDictionaryStatus,
        CompositionIndex,
        DeduplicateOverlayMeter,
        DeferSettingsPageData,
        DirectGlossaryProjection,
        HookPriorityAboveNormal,
        HookRecoveryObservations,
        IncrementalWordPackRows,
        InputTimings,
        LeanFooterRefresh,
        LeanHostDefaults,
        MatcherPrefilter,
        MatcherSpans,
        PillBeforeTray,
        PreciseLocalTypingSettle,
        ReuseStatusComposition,
        ServiceLocalHookProbe,
        SkipVelopackWhenPackaged,
        SnapshotInjectionLayout,
        SparseUsageAggregation,
        StartupStageTiming,
        StopInactiveProgress,
        UsageTermIndex,
        VadWindowCancellation,
        WarmManagedAudioPath,
    ];

    // The flags, one constant per name (0.5.1). What each changes, and the old path it keeps when off, is on the
    // implementation that reads it.
    public const string AggressiveIdleGc = nameof(AggressiveIdleGc);
    public const string AsyncDeviceList = nameof(AsyncDeviceList);
    public const string BatchCleanupSelectionCounts = nameof(BatchCleanupSelectionCounts);
    public const string BoundedDiagnosticsReads = nameof(BoundedDiagnosticsReads);
    public const string BoundedUsageTokenLookup = nameof(BoundedUsageTokenLookup);
    public const string CachedRowSearchText = nameof(CachedRowSearchText);
    public const string CacheWordPackPreview = nameof(CacheWordPackPreview);
    public const string CacheWordPackSort = nameof(CacheWordPackSort);
    public const string CaptureTimingDiagnostics = nameof(CaptureTimingDiagnostics);
    public const string CleanupPhaseTelemetry = nameof(CleanupPhaseTelemetry);
    public const string CliAccessTokenCache = nameof(CliAccessTokenCache);
    public const string CoalesceDictionaryStatus = nameof(CoalesceDictionaryStatus);
    public const string CompositionIndex = nameof(CompositionIndex);
    public const string DeduplicateOverlayMeter = nameof(DeduplicateOverlayMeter);
    public const string DeferSettingsPageData = nameof(DeferSettingsPageData);
    public const string DirectGlossaryProjection = nameof(DirectGlossaryProjection);
    public const string HookPriorityAboveNormal = nameof(HookPriorityAboveNormal);
    public const string HookRecoveryObservations = nameof(HookRecoveryObservations);
    public const string IncrementalWordPackRows = nameof(IncrementalWordPackRows);
    public const string InputTimings = nameof(InputTimings);
    public const string LeanFooterRefresh = nameof(LeanFooterRefresh);
    public const string LeanHostDefaults = nameof(LeanHostDefaults);
    public const string MatcherPrefilter = nameof(MatcherPrefilter);
    public const string MatcherSpans = nameof(MatcherSpans);
    public const string PillBeforeTray = nameof(PillBeforeTray);
    public const string PreciseLocalTypingSettle = nameof(PreciseLocalTypingSettle);
    public const string ReuseStatusComposition = nameof(ReuseStatusComposition);
    public const string ServiceLocalHookProbe = nameof(ServiceLocalHookProbe);
    public const string SkipVelopackWhenPackaged = nameof(SkipVelopackWhenPackaged);
    public const string SnapshotInjectionLayout = nameof(SnapshotInjectionLayout);
    public const string SparseUsageAggregation = nameof(SparseUsageAggregation);
    public const string StartupStageTiming = nameof(StartupStageTiming);
    public const string StopInactiveProgress = nameof(StopInactiveProgress);
    public const string UsageTermIndex = nameof(UsageTermIndex);
    public const string VadWindowCancellation = nameof(VadWindowCancellation);
    public const string WarmManagedAudioPath = nameof(WarmManagedAudioPath);

    private readonly HashSet<string> _on;

    private PerfFlags(IReadOnlyList<string> on, int unknownCount)
    {
        On = on;
        _on = new HashSet<string>(on, StringComparer.OrdinalIgnoreCase);
        UnknownCount = unknownCount;
    }

    /// <summary>Every flag off.</summary>
    public static PerfFlags None { get; } = new([], 0);

    /// <summary>Every flag name this build knows.</summary>
    public static IReadOnlyList<string> Known => KnownNames;

    /// <summary>How many names in the variable this build does not know.</summary>
    public int UnknownCount { get; }

    /// <summary>The flags that are on, in <see cref="Known"/> order.</summary>
    public IReadOnlyList<string> On { get; }

    /// <summary>Whether the named flag is on.</summary>
    public bool IsOn(string name) => _on.Contains(name);

    /// <summary>Reads the flags from this process's environment.</summary>
    public static PerfFlags FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Parses a value of <see cref="EnvironmentVariable"/> against the flags this build knows.</summary>
    public static PerfFlags Parse(string? value) => Parse(value, KnownNames);

    internal static PerfFlags Parse(string? value, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        var knownSet = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (knownSet.Contains(name) ? requested : unknown).Add(name);
        }

        if (requested.Count == 0 && unknown.Count == 0)
        {
            return None;
        }

        // Known order and the known spelling, whatever order and case the variable used.
        return new PerfFlags([.. known.Where(requested.Contains)], unknown.Count);
    }

    /// <summary>The banner's shape: the names that are on (or "none") and how many names were not recognised.</summary>
    public string Describe()
    {
        var on = On;
        var names = on.Count == 0 ? "none" : string.Join(',', on);
        return UnknownCount == 0 ? $"flags={names}" : $"flags={names} unknown={UnknownCount}";
    }
}
