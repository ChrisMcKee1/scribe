using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Scribe.App.Infrastructure;

/// <summary>
/// An OpenTelemetry span processor that writes each finished <see cref="Activity"/> from Scribe's
/// dictation source to the app's normal file log. A tray app has no console and most users will
/// never run an OTLP collector, so this guarantees the lifecycle trace is visible in
/// <c>%LOCALAPPDATA%\ScribeData\logs</c> with zero setup — turning an intermittent "the text didn't
/// appear" into a single readable line that names the exact stage and its tags.
/// </summary>
internal sealed class LogTraceProcessor : BaseProcessor<Activity>
{
    private const string ScribeTagPrefix = "scribe.";

    private readonly ILogger _log;

    public LogTraceProcessor(ILoggerFactory loggerFactory) =>
        _log = loggerFactory.CreateLogger("Scribe.Trace");

    public override void OnEnd(Activity activity)
    {
        var builder = new StringBuilder(activity.OperationName);
        foreach (var tag in activity.TagObjects)
        {
            var key = tag.Key.StartsWith(ScribeTagPrefix, StringComparison.Ordinal)
                ? tag.Key[ScribeTagPrefix.Length..]
                : tag.Key;
            builder.Append(' ').Append(key).Append('=').Append(tag.Value);
        }

        builder.Append(" (").Append((int)activity.Duration.TotalMilliseconds).Append("ms)");

        // Surface error spans (e.g. a partial SendInput) at Warning so they're easy to spot.
        if (activity.Status == ActivityStatusCode.Error)
        {
            var detail = string.IsNullOrEmpty(activity.StatusDescription) ? string.Empty : " — " + activity.StatusDescription;
            _log.LogWarning("trace {Span}{Detail}", builder.ToString(), detail);
        }
        else
        {
            _log.LogInformation("trace {Span}", builder.ToString());
        }
    }
}
