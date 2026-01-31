using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Shared.Observability
{
    // Shared OpenTelemetry bootstrap for console apps and libraries.
    // Best practices:
    // https://opentelemetry.io/docs/specs/otel/
    // https://opentelemetry.io/docs/specs/otlp/
    public static class Observability
    {
        // Configuration container for shared telemetry settings.
        public sealed class ObservabilityOptions
        {
            public string OtlpEndpoint { get; set; } = "http://localhost:18890";
            // Valid values: "http/protobuf" or "grpc".
            public string OtlpProtocol { get; set; } = "http/protobuf";
            public int MetricExportIntervalMilliseconds { get; set; } = 5000;
            public string SystemName { get; set; } = "OnlineSalesMotorSE";
            public string SystemCode { get; set; } = "MOTOR";
            public string DeploymentEnvironment { get; set; } = "local";
            public bool EnableConsoleMetrics { get; set; } = false;
        }

        // ActivitySource used across the solution for custom spans.
        public const string ActivitySourceName = "PerfReference";
        // Meter used across the solution for custom metrics.
        public const string MeterName = "PerfReference.Metrics";
        public static readonly ActivitySource ActivitySource = new ActivitySource(ActivitySourceName);
        public static readonly Meter Meter = new Meter(MeterName);

        // Resource attributes applied to all telemetry signals.
        // Semantic conventions:
        // https://opentelemetry.io/docs/specs/semconv/resource/
        public static ResourceBuilder CreateResourceBuilder(string serviceName, ObservabilityOptions options)
        {
            var resolved = options ?? new ObservabilityOptions();
            return ResourceBuilder.CreateDefault()
                .AddService(serviceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["system.name"] = resolved.SystemName,
                    ["system.code"] = resolved.SystemCode,
                    ["deployment.environment"] = resolved.DeploymentEnvironment,
                });
        }

        public static (TracerProvider TracerProvider, MeterProvider MeterProvider) Start(
            string serviceName,
            ObservabilityOptions options,
            Action<TracerProviderBuilder> configureTracing = null,
            Action<MeterProviderBuilder> configureMetrics = null)
        {
            // Build a shared resource for traces and metrics so they correlate in the backend.
            var resolved = options ?? new ObservabilityOptions();
            var resourceBuilder = CreateResourceBuilder(serviceName, resolved);

            // Traces: register ActivitySource and export via OTLP/HTTP-Protobuf to Aspire.
            var tracerProviderBuilder = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(exporterOptions =>
                {
                    exporterOptions.Endpoint = new Uri(resolved.OtlpEndpoint);
                    exporterOptions.Protocol = ResolveOtlpProtocol(resolved.OtlpProtocol);
                });

            configureTracing?.Invoke(tracerProviderBuilder);
            var tracerProvider = tracerProviderBuilder.Build();

            // Metrics: register Meter and export via OTLP/HTTP-Protobuf to Aspire.
            var meterProviderBuilder = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(MeterName);

            // Periodic metric reader with explicit export interval for short-lived processes.
            var metricExporter = new OpenTelemetry.Exporter.OtlpMetricExporter(new OpenTelemetry.Exporter.OtlpExporterOptions
            {
                Endpoint = new Uri(resolved.OtlpEndpoint),
                Protocol = ResolveOtlpProtocol(resolved.OtlpProtocol),
            });
            meterProviderBuilder.AddReader(new PeriodicExportingMetricReader(
                metricExporter,
                exportIntervalMilliseconds: resolved.MetricExportIntervalMilliseconds));

            if (resolved.EnableConsoleMetrics)
            {
                // Console exporter is a debugging aid to verify metrics are produced locally.
                meterProviderBuilder.AddConsoleExporter();
            }

            configureMetrics?.Invoke(meterProviderBuilder);
            var meterProvider = meterProviderBuilder.Build();

            return (tracerProvider, meterProvider);
        }

        public static (TracerProvider TracerProvider, MeterProvider MeterProvider) Start(
            string serviceName,
            Action<TracerProviderBuilder> configureTracing = null,
            Action<MeterProviderBuilder> configureMetrics = null)
        {
            return Start(serviceName, new ObservabilityOptions(), configureTracing, configureMetrics);
        }

        private static OpenTelemetry.Exporter.OtlpExportProtocol ResolveOtlpProtocol(string protocol)
        {
            if (string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            }

            return OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }
    }
}
