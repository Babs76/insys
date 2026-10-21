using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Insys.Api.Platform;

internal static class Telemetry
{
    /// <summary>
    /// OpenTelemetry traces, metrics and logs. Exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// Request bodies and SQL parameters are never recorded, so medical and identity values stay out of telemetry (HLT-008).
    /// </summary>
    public static WebApplicationBuilder AddInsysTelemetry(this WebApplicationBuilder builder)
    {
        var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("insys-core-api"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddSource("Npgsql");
                if (otlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentationIfAvailable();
                if (otlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            if (otlp)
            {
                logging.AddOtlpExporter();
            }
        });

        _ = telemetry;
        return builder;
    }

    private static MeterProviderBuilder AddRuntimeInstrumentationIfAvailable(this MeterProviderBuilder metrics) =>
        metrics.AddMeter("System.Runtime");
}
