using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Infrastructure;

/// <summary>
/// An OpenTelemetry span processor that writes each finished <see cref="Activity"/> from Scribe's
/// dictation source to the app's normal file log. A tray app has no console and most users will
/// never run an OTLP collector, so this guarantees the lifecycle trace is visible in
/// <c>%LOCALAPPDATA%\ScribeData\logs</c> with zero setup, turning an intermittent "the text didn't
/// appear" into a single readable line that names the exact stage and its tags.
/// <para>
/// Only allowlisted tags are rendered, each checked against its expected value shape
/// (<see cref="TraceTagPolicy"/>). The bridge used to append every tag value verbatim, which is how a
/// custom endpoint's host name inside <c>ai_skip_reason</c> reached the file; unknown tags are now
/// counted and left out, key and value.
/// </para>
/// </summary>
internal sealed class LogTraceProcessor : BaseProcessor<Activity>
{
    private readonly ILogger _log;

    public LogTraceProcessor(ILoggerFactory loggerFactory) =>
        _log = loggerFactory.CreateLogger("Scribe.Trace");

    public override void OnEnd(Activity activity)
    {
        // OnEnd runs inside Activity.Stop on the dictation path, so the bridge must never fail the span
        // it is describing.
        try
        {
            var span = TraceTagPolicy.FormatSpan(activity.OperationName, activity.TagObjects, activity.Duration);

            // Surface error spans (e.g. a partial SendInput) at Warning so they're easy to spot.
            if (activity.Status == ActivityStatusCode.Error)
            {
                _log.LogWarning("trace {Span}{Detail}", span, TraceTagPolicy.FormatStatusDetail(activity.StatusDescription));
            }
            else
            {
                _log.LogInformation("trace {Span}", span);
            }
        }
        catch (Exception)
        {
            // Diagnostics are best-effort.
        }
    }
}
