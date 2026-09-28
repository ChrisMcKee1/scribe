using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Scribe.Core.Diagnostics;

/// <summary>Why a launch did or did not share the log append-only (a shape the client logs).</summary>
public enum AppendOnlyLaunchDecision
{
    /// <summary>Nothing has been decided yet.</summary>
    None,

    /// <summary>Both append only.</summary>
    AppendOnly,

    /// <summary>The flag is off.</summary>
    NotRequested,

    /// <summary>The helper's payload does not declare that it follows the launch argument, or cannot be read.</summary>
    HelperNotCapable,

    /// <summary>The helper's informational version is not the app's, or either is unknown.</summary>
    OtherBuild,

    /// <summary>
    /// A write the other way was still in progress when the helper was due to start: the app keeps its way, and the helper
    /// starts only if it can append that way (<see cref="SharedLogLaunch"/>).
    /// </summary>
    WriteInProgress,

    /// <summary>
    /// A helper the app ended had not been seen to exit: the app keeps its way, the helper starts only if it can append that
    /// way, and a later launch tries again.
    /// </summary>
    PreviousHelperRunning,

    /// <summary>
    /// An earlier launch of this session ran the old way, so this one does too: the pair becomes append-only only at the
    /// session's first launch.
    /// </summary>
    StaysOldWay,
}

/// <summary>What a launch does with the helper (<see cref="AppendOnlyLogMode.DecideForLaunch"/>): one way for the pair, or none.</summary>
public enum SharedLogLaunch
{
    /// <summary>Start the helper without <see cref="AppendOnlyLogMode.LaunchArgument"/>: both append the old way.</summary>
    OldWay,

    /// <summary>Start the helper with <see cref="AppendOnlyLogMode.LaunchArgument"/>: both append only.</summary>
    AppendOnly,

    /// <summary>
    /// Start no helper now: the app appends only and could not move back in time, and this helper cannot append only, so
    /// no way can be agreed yet. The client counts it as a failed launch; the next attempt decides again.
    /// </summary>
    Refused,
}

/// <summary>
/// The one way the app and the overlay helper it launches append to the shared daily log (DATA-O-02,
/// <see cref="PerfFlags.AppendOnlyLog"/>).
/// </summary>
/// <remarks>
/// <para>
/// A pair must never run two ways: a <see cref="FileMode.Append"/> writer can still write over the other's append-only
/// lines. So the app decides, for each helper it launches, and the helper follows the launch argument
/// (<see cref="LaunchArgument"/>), never its own environment: append-only only when the flag is on, the helper's own
/// payload declares that it follows the argument (<see cref="CapabilityKey"/>, an assembly metadata entry the overlay
/// build carries since the argument exists), and both builds' informational versions are readable and equal. Anything
/// else keeps <see cref="FileMode.Append"/> for both.
/// </para>
/// <para>
/// The switch retires every physical write of the other way before the helper starts (DATA-IMPL-A-01). Each write the
/// app's log writer makes announces itself and then reads the mode (<see cref="BeginWrite"/>), and the switch sets the
/// mode and then waits for the writes of the other way to end: with full fences on both sides, either the switch sees
/// the write and waits for its close, or the write sees the new mode. The writer never waits and takes no lock; only the
/// launcher waits, bounded, and when a write of the other way does not end in time the app keeps the way that write is
/// part of.
/// </para>
/// <para>
/// A helper the app ended writes too until it has exited, so the way never changes while one may still be running
/// (<see cref="RetiringHelpers"/>, waited for on the launcher's thread, bounded). And the pair becomes append-only only at a
/// session's first launch, before any helper has run: once it has run the old way, whether a write held the switch back, a
/// helper could not append, or the app moved the pair back, it stays so until the app starts again
/// (<see cref="AppendOnlyLaunchDecision.StaysOldWay"/>). While the app appends only, a helper that does not qualify (another
/// build, no capability entry, or a payload that cannot be read) moves both back to the old way.
/// </para>
/// <para>
/// Every launch ends with one way for the pair or no launch (<see cref="SharedLogLaunch"/>). When the way cannot change in
/// time, the app keeps its way and the helper starts only if it can append that way: every helper can append the old way,
/// and a helper that declares the capability can append only; one that cannot append the app's way is not started
/// (<see cref="SharedLogLaunch.Refused"/>): the client counts that as a failed launch, so its cooldown and its one retry
/// decide when the next launch decides again, reading the payload afresh. The app's way is never forced to agree with a
/// helper, and no helper is ever started to append another way than the app.
/// </para>
/// </remarks>
public sealed class AppendOnlyLogMode
{
    /// <summary>The overlay launch argument the pair's mode travels in.</summary>
    public const string LaunchArgument = AppendOnlyFile.LaunchArgument;

    /// <summary>
    /// The assembly metadata key an overlay build that follows <see cref="LaunchArgument"/> declares, with
    /// <see cref="CapabilityValue"/>. Introduced with the argument, so a build without it predates it.
    /// </summary>
    public const string CapabilityKey = "Scribe.SharedLogAppendOnly";

    /// <summary>The only value of <see cref="CapabilityKey"/> that declares the capability.</summary>
    public const string CapabilityValue = "1";

    /// <summary>How long a switch waits for a write of the other way to end before the pair keeps its way.</summary>
    public static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a launch that would change the pair's way waits for the helpers the app ended to exit.</summary>
    public static readonly TimeSpan RetirementTimeout = TimeSpan.FromSeconds(2);

    private const string OverlayAssemblyFileName = "Scribe.Overlay.dll";
    private const string InformationalVersionAttribute = "System.Reflection.AssemblyInformationalVersionAttribute";
    private const string MetadataAttribute = "System.Reflection.AssemblyMetadataAttribute";

    // 0 today's stream, 1 append-only. Changed only by a switch.
    private int _mode;

    // Physical writes in progress, by the way each opened.
    private readonly int[] _writesInProgress = new int[2];

    private int _lastDecision;

    // 1 once a launch has been decided: only the first can make the pair append-only.
    private int _decided;

    /// <param name="requested">Whether <see cref="PerfFlags.AppendOnlyLog"/> is on.</param>
    /// <param name="appVersion">The app's own informational version (<see cref="ReadVersion"/>).</param>
    public AppendOnlyLogMode(bool requested, string? appVersion)
    {
        Requested = requested;
        AppVersion = appVersion;
    }

    /// <summary>Whether <see cref="PerfFlags.AppendOnlyLog"/> asked for append-only writes.</summary>
    public bool Requested { get; }

    /// <summary>The app's own informational version, compared with each helper's.</summary>
    public string? AppVersion { get; }

    /// <summary>Whether the app's writer appends only, now.</summary>
    public bool AppendOnly => Volatile.Read(ref _mode) == 1;

    /// <summary>What the last launch decided, and why.</summary>
    public AppendOnlyLaunchDecision LastDecision => (AppendOnlyLaunchDecision)Volatile.Read(ref _lastDecision);

    /// <summary>A mode set once, for tests and tools that write without an overlay.</summary>
    public static AppendOnlyLogMode Fixed(bool appendOnly) => new(appendOnly, null) { _mode = appendOnly ? 1 : 0 };

    /// <summary>
    /// Starts one physical write (one open, write and close): the way to open, counted until the result is disposed,
    /// which must be after the stream is closed. Never waits and takes no lock.
    /// </summary>
    public Write BeginWrite()
    {
        while (true)
        {
            var mode = Volatile.Read(ref _mode);
            AfterModeRead?.Invoke();
            Interlocked.Increment(ref _writesInProgress[mode]);
            if (Volatile.Read(ref _mode) == mode)
            {
                return new Write(this, mode);
            }

            // A switch landed between the read and the announcement: take the new way instead.
            Interlocked.Decrement(ref _writesInProgress[mode]);
        }
    }

    /// <summary>
    /// Test seam: runs in <see cref="BeginWrite"/> between reading the mode and announcing the write, where a switch can
    /// land unseen by either side but for the second read. Unset in the app.
    /// </summary>
    internal Action? AfterModeRead { get; set; }

    /// <summary>
    /// Decides the pair's way for a launch of the helper at <paramref name="overlayExecutable"/>, applies it to this app's
    /// writer, and returns what the launch does: start the helper the old way, start it with <see cref="LaunchArgument"/>,
    /// or start none (<see cref="SharedLogLaunch.Refused"/>). Called on the launching thread before the helper starts. The
    /// way changes only once every helper in <paramref name="endedHelpers"/> (the ones the app ended, none when null) has been
    /// seen to exit and every write of the other way has ended, each waited for here, bounded by
    /// <see cref="RetirementTimeout"/> and <see cref="HandoverTimeout"/>; nothing is waited for when the way stays. The
    /// helper's payload is read again on every call.
    /// </summary>
    public SharedLogLaunch DecideForLaunch(string? overlayExecutable, RetiringHelpers? endedHelpers = null)
    {
        var payload = ReadHelperPayload(overlayExecutable);
        var wanted = Decide(Requested, AppVersion, payload);
        var wantAppendOnly = wanted == AppendOnlyLaunchDecision.AppendOnly;
        var appendOnly = AppendOnly;
        var first = Interlocked.Exchange(ref _decided, 1) == 0;

        // The ended helpers already gone are released now, whatever this launch decides.
        var retired = endedHelpers?.WaitForAllToExit(TimeSpan.Zero) ?? true;
        AppendOnlyLaunchDecision decision;
        if (wantAppendOnly == appendOnly)
        {
            decision = wanted;
        }
        else if (wantAppendOnly && !first)
        {
            decision = AppendOnlyLaunchDecision.StaysOldWay;
        }
        else if (!retired && !endedHelpers!.WaitForAllToExit(RetirementTimeout))
        {
            decision = AppendOnlyLaunchDecision.PreviousHelperRunning;
        }
        else if (SwitchTo(wantAppendOnly, HandoverTimeout) == wantAppendOnly)
        {
            appendOnly = wantAppendOnly;
            decision = wanted;
        }
        else
        {
            decision = AppendOnlyLaunchDecision.WriteInProgress;
        }

        Volatile.Write(ref _lastDecision, (int)decision);

        // Every helper can append the old way. While the app appends only, a helper that declares the capability does too
        // (another build, launched while the move back is held back); one that cannot is not started.
        if (!appendOnly)
        {
            return SharedLogLaunch.OldWay;
        }

        return payload.DeclaresCapability ? SharedLogLaunch.AppendOnly : SharedLogLaunch.Refused;
    }

    /// <summary>
    /// The rule: append-only only when requested, the helper declares the capability, and both builds' informational
    /// versions are known and equal. Anything that cannot be established keeps today's way.
    /// </summary>
    public static AppendOnlyLaunchDecision Decide(bool requested, string? appVersion, HelperPayload helper)
    {
        if (!requested)
        {
            return AppendOnlyLaunchDecision.NotRequested;
        }

        if (!helper.DeclaresCapability)
        {
            return AppendOnlyLaunchDecision.HelperNotCapable;
        }

        return !string.IsNullOrWhiteSpace(appVersion) && string.Equals(appVersion, helper.InformationalVersion, StringComparison.Ordinal)
            ? AppendOnlyLaunchDecision.AppendOnly
            : AppendOnlyLaunchDecision.OtherBuild;
    }

    /// <summary>
    /// Makes <paramref name="appendOnly"/> the app writer's way once no write of the other way is in progress, waiting at
    /// most <paramref name="timeout"/>. Returns the way in effect: when a write of the other way does not end in time, the
    /// way it had, which that write is part of.
    /// </summary>
    internal bool SwitchTo(bool appendOnly, TimeSpan timeout)
    {
        var target = appendOnly ? 1 : 0;
        var previous = Interlocked.Exchange(ref _mode, target);
        if (previous == target || WaitForWritesToEnd(previous, timeout))
        {
            return appendOnly;
        }

        Interlocked.Exchange(ref _mode, previous);
        return previous == 1;
    }

    private bool WaitForWritesToEnd(int mode, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        var spinner = default(SpinWait);
        while (Volatile.Read(ref _writesInProgress[mode]) != 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            spinner.SpinOnce();
        }

        return true;
    }

    /// <summary>What the helper beside <paramref name="overlayExecutable"/> declares, read from its payload's metadata.</summary>
    public static HelperPayload ReadHelperPayload(string? overlayExecutable)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(overlayExecutable) ? null : Path.GetDirectoryName(overlayExecutable);
            return directory is null ? HelperPayload.Unreadable : ReadPayload(Path.Combine(directory, OverlayAssemblyFileName));
        }
        catch (Exception)
        {
            // A path that cannot be read is a helper that cannot be relied on: the pair keeps today's way.
            return HelperPayload.Unreadable;
        }
    }

    /// <summary>The informational version of the overlay build beside <paramref name="overlayExecutable"/>, or null.</summary>
    public static string? ReadOverlayVersion(string? overlayExecutable) => ReadHelperPayload(overlayExecutable).InformationalVersion;

    /// <summary>The informational version the assembly file at <paramref name="assemblyPath"/> carries, or null.</summary>
    public static string? ReadVersion(string? assemblyPath) =>
        string.IsNullOrWhiteSpace(assemblyPath) ? null : ReadPayload(assemblyPath).InformationalVersion;

    // Reads the assembly's metadata without loading it: its informational version and whether it declares the capability.
    private static HelperPayload ReadPayload(string assemblyPath)
    {
        try
        {
            if (!File.Exists(assemblyPath))
            {
                return HelperPayload.Unreadable;
            }

            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return HelperPayload.Unreadable;
            }

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return HelperPayload.Unreadable;
            }

            string? version = null;
            var capable = false;
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                var type = AttributeTypeName(metadata, attribute);
                if (type == InformationalVersionAttribute && ReadStrings(metadata, attribute, 1) is [var informational])
                {
                    version = string.IsNullOrWhiteSpace(informational) ? null : informational;
                }
                else if (type == MetadataAttribute && ReadStrings(metadata, attribute, 2) is [var key, var value] &&
                    string.Equals(key, CapabilityKey, StringComparison.Ordinal) &&
                    string.Equals(value, CapabilityValue, StringComparison.Ordinal))
                {
                    capable = true;
                }
            }

            return new HelperPayload(version, capable);
        }
        catch (Exception)
        {
            return HelperPayload.Unreadable;
        }
    }

    private static string? AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
    {
        EntityHandle type;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                type = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                type = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            default:
                return null;
        }

        switch (type.Kind)
        {
            case HandleKind.TypeReference:
                var reference = metadata.GetTypeReference((TypeReferenceHandle)type);
                return metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);
            case HandleKind.TypeDefinition:
                var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)type);
                return metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);
            default:
                return null;
        }
    }

    // The constructor's leading string arguments, from the attribute's value blob (ECMA-335 II.23.3).
    private static string?[]? ReadStrings(MetadataReader metadata, CustomAttribute attribute, int count)
    {
        var reader = metadata.GetBlobReader(attribute.Value);
        if (reader.Length < 2 || reader.ReadUInt16() != 1)
        {
            return null;
        }

        var values = new string?[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.ReadSerializedString();
        }

        return values;
    }

    /// <summary>One physical write in progress: which way it opened. Dispose it once the stream is closed.</summary>
    public readonly struct Write : IDisposable
    {
        private readonly AppendOnlyLogMode? _owner;
        private readonly int _mode;

        internal Write(AppendOnlyLogMode owner, int mode)
        {
            _owner = owner;
            _mode = mode;
        }

        /// <summary>Whether this write opens append-only.</summary>
        public bool AppendOnly => _mode == 1;

        public void Dispose()
        {
            if (_owner is not null)
            {
                Interlocked.Decrement(ref _owner._writesInProgress[_mode]);
            }
        }
    }
}

/// <summary>What an overlay helper's payload declares (<see cref="AppendOnlyLogMode.ReadHelperPayload"/>).</summary>
/// <param name="InformationalVersion">Its informational version, or null when unknown.</param>
/// <param name="DeclaresCapability">Whether it declares that it follows <see cref="AppendOnlyLogMode.LaunchArgument"/>.</param>
public readonly record struct HelperPayload(string? InformationalVersion, bool DeclaresCapability)
{
    /// <summary>A payload that could not be read: nothing is established.</summary>
    public static HelperPayload Unreadable => default;
}
