/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Api.Host
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
                tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");

                        // ADR-023: a provider callback's path holds its endpoint key, which is never recorded.
                        options.EnrichWithHttpRequest = (activity, request) =>
                        {
                            if (request.Path.Value?.Contains("/payments/callbacks/", StringComparison.Ordinal) == true)
                            {
                                activity.SetTag("url.path", "/api/v1/payments/callbacks/{endpointKey}");
                                activity.SetTag("url.full", null);
                            }
                        };
                    })
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
