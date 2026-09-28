using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
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
    /// backend is running. Both paths carry only the tags <see cref="TraceTagPolicy"/> allows.
    /// <para>
    /// With <see cref="PerfFlags.LightTraceBridge"/> on and nothing configuring OpenTelemetry (no <c>OTEL_</c> setting in
    /// the environment or the host's configuration, no self-diagnostics file), the same lines come from
    /// <see cref="TraceLogListener"/> instead, which decides as the SDK would without building it (DATA-O-03). Any such
    /// setting keeps the SDK, so the exporter, its configuration and <see cref="TraceTagScrubProcessor"/> never change.
    /// </para>
    /// </summary>
    public static IServiceCollection AddScribeTelemetry(
        this IServiceCollection services, PerfFlags? flags = null, IConfiguration? configuration = null)
    {
        if (flags?.IsOn(PerfFlags.LightTraceBridge) == true && !OpenTelemetryConfigured(configuration))
        {
            services.AddHostedService<TraceLogListener>();
            return services;
        }

        return AddOpenTelemetrySdk(services);
    }

    // Kept apart, and never inlined, so the OpenTelemetry assemblies load only when this runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IServiceCollection AddOpenTelemetrySdk(IServiceCollection services)
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
                    // Processors run in the order they are added, so the scrub sits after the log
                    // bridge and before the exporter: only allowlisted tag values reach a collector.
                    tracing.AddProcessor(_ => new TraceTagScrubProcessor());
                    tracing.AddOtlpExporter();
                }
            });

        return services;
    }

    private static bool OpenTelemetryConfigured(IConfiguration? configuration)
    {
        var names = new List<string>();
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name)
            {
                names.Add(name);
            }
        }

        if (configuration is not null)
        {
            foreach (var setting in configuration.AsEnumerable())
            {
                names.Add(setting.Key);
            }
        }

        return TraceLogBridge.OpenTelemetryConfigured(
            names, [Environment.CurrentDirectory, AppContext.BaseDirectory], System.IO.File.Exists);
    }
}
