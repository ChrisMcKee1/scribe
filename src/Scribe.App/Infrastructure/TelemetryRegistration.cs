using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Infrastructure;

/// <summary>Registers Scribe's OpenTelemetry tracing into the host.</summary>
internal static class TelemetryRegistration
{
    /// <summary>
    /// Wires up tracing for the dictation pipeline. The <see cref="ScribeTelemetry.SourceName"/>
    /// source is always bridged to the file log via <see cref="LogTraceProcessor"/>, so the
    /// lifecycle is inspectable out of the box. A full OTLP exporter is added only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, so power users can stream traces to an Aspire
    /// dashboard, Jaeger or any collector without the exporter spamming connection errors when no
    /// backend is running.
    /// </summary>
    public static IServiceCollection AddScribeTelemetry(this IServiceCollection services)
    {
        var version = typeof(TelemetryRegistration).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Scribe", serviceVersion: version))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ScribeTelemetry.SourceName);
                tracing.AddProcessor(sp => new LogTraceProcessor(sp.GetRequiredService<ILoggerFactory>()));

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            });

        return services;
    }
}
