using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Writes Scribe's finished spans to the file log, for both ways the app can collect them: the OpenTelemetry SDK's
/// processor (<c>LogTraceProcessor</c>, today's path) and, with <see cref="PerfFlags.LightTraceBridge"/> and nothing
/// configuring OpenTelemetry, a plain <see cref="ActivityListener"/> that decides exactly as the SDK would (DATA-O-03).
/// </summary>
/// <remarks>
/// With no OTLP endpoint the SDK's only consumer is the log bridge, yet building its TracerProvider on the UI thread at
/// startup loads and compiles the SDK's assemblies. <see cref="Listen"/> reproduces what OpenTelemetry 1.18.0's
/// TracerProviderSdk does in that configuration: it listens to the source by name without regard to case, samples with the
/// SDK's default ParentBased(AlwaysOn) through the same mapping (a root, or a parent whose flags say recorded, is recorded
/// with all its data; a child of an unrecorded parent is dropped, which the SDK turns into PropagationData for a remote
/// parent and None for a local one), sets no SampleUsingParentId (neither does the SDK), sets the same two
/// <see cref="Activity"/> id statics the SDK's static constructor sets, and bridges a stopped activity only when
/// <see cref="Activity.IsAllDataRequested"/>. The SDK's suppression scopes are OpenTelemetry's own; with none of its
/// assemblies loaded, nothing can enter one.
/// </remarks>
public static class TraceLogBridge
{
    /// <summary>The log category every bridged span is written under.</summary>
    public const string Category = "Scribe.Trace";

    /// <summary>
    /// Writes <paramref name="activity"/> as one line: its operation, allowlisted tags (<see cref="TraceTagPolicy"/>) and
    /// duration, at Warning for an error span. Runs inside <see cref="Activity.Stop"/> on the dictation path, so it never
    /// throws.
    /// </summary>
    public static void Write(ILogger log, Activity activity)
    {
        try
        {
            var span = TraceTagPolicy.FormatSpan(activity.OperationName, activity.TagObjects, activity.Duration);

            // Surface error spans (e.g. a partial SendInput) at Warning so they're easy to spot.
            if (activity.Status == ActivityStatusCode.Error)
            {
                TraceLogMessages.TraceWarning(log, span, TraceTagPolicy.FormatStatusDetail(activity.StatusDescription));
            }
            else
            {
                TraceLogMessages.TraceInformation(log, span);
            }
        }
        catch (Exception)
        {
            // Diagnostics are best-effort.
        }
    }

    /// <summary>
    /// The OpenTelemetry SDK's sampling decision for its default sampler, ParentBased(AlwaysOn), with no suppression
    /// scope (TracerProviderSdk.ComputeActivitySamplingResult and PropagateOrIgnoreData in 1.18.0).
    /// </summary>
    public static ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        // The SDK reads the trace id before it decides (its SamplingParameters take it). For a root that read gives the
        // options a trace id, which the runtime then builds the activity from, dropping a parent id it could not parse:
        // read it the same way, so every activity comes out as the SDK's would.
        _ = options.TraceId;

        var parent = options.Parent;
        if (parent.TraceId == default || (parent.TraceFlags & ActivityTraceFlags.Recorded) != 0)
        {
            return ActivitySamplingResult.AllDataAndRecorded;
        }

        return parent.IsRemote ? ActivitySamplingResult.PropagationData : ActivitySamplingResult.None;
    }

    /// <summary>
    /// Starts bridging <paramref name="sourceName"/>'s finished spans to <paramref name="log"/> the way the SDK's log
    /// processor would. Dispose the result to stop, as disposing the SDK's TracerProvider stops it.
    /// </summary>
    public static IDisposable Listen(ILogger log, string sourceName = ScribeTelemetry.SourceName)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(sourceName);

        // What OpenTelemetry's Sdk static constructor sets for the whole process, so ids come out as they would with it.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.OrdinalIgnoreCase),
            Sample = Sample,
            ActivityStopped = activity =>
            {
                if (activity.IsAllDataRequested)
                {
                    Write(log, activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Whether anything configures OpenTelemetry for this process, in which case the SDK stays in charge as it always
    /// has: any setting whose name starts with <c>OTEL_</c> (an environment variable, or a configuration key, which the
    /// SDK reads through the host's configuration), or an <c>OTEL_DIAGNOSTICS.json</c> in a folder its self-diagnostics
    /// reads (the working directory and the app's own).
    /// </summary>
    public static bool OpenTelemetryConfigured(
        IEnumerable<string> settingNames, IEnumerable<string> diagnosticsFolders, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(settingNames);
        ArgumentNullException.ThrowIfNull(diagnosticsFolders);
        ArgumentNullException.ThrowIfNull(fileExists);

        foreach (var name in settingNames)
        {
            if (name is not null && name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var folder in diagnosticsFolders)
        {
            if (!string.IsNullOrEmpty(folder) && fileExists(Path.Combine(folder, "OTEL_DIAGNOSTICS.json")))
            {
                return true;
            }
        }

        return false;
    }
}

// The two lines a bridged span is written as, source-generated (perf-051's change to LogTraceProcessor, moved here with its
// body so the SDK's processor and the listener both write through them): the same event ids, levels and templates.
internal static partial class TraceLogMessages
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Warning, Message = "trace {Span}{Detail}")]
    public static partial void TraceWarning(ILogger logger, string span, string detail);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "trace {Span}")]
    public static partial void TraceInformation(ILogger logger, string span);
}
