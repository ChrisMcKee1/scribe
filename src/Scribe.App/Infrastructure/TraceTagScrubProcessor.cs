using System.Diagnostics;
using OpenTelemetry;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Applies <see cref="TraceTagPolicy"/> to each finished dictation span before an OTLP exporter sees it:
/// tags that are not on the allowlist are removed, and allowed tags whose value does not have the
/// expected shape are replaced with <see cref="TraceTagPolicy.OmittedValue"/>. Registered only when an
/// exporter is, and after the file log bridge, which applies the same policy when it renders.
/// </summary>
internal sealed class TraceTagScrubProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        // Runs inside Activity.Stop on the dictation path; it must never fail the span.
        try
        {
            // The plan is materialized first: changing tags while enumerating them is not allowed.
            foreach (var change in TraceTagPolicy.PlanExportScrub(activity.TagObjects))
            {
                activity.SetTag(change.Key, change.Value);
            }
        }
        catch (Exception)
        {
            // Diagnostics are best-effort.
        }
    }
}
