using System.Collections.Generic;
using System.Diagnostics;
using Api.Net10.Models;
using Api.Net10.Services;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

// Load HTTP and observability settings from configuration.
var restPort = builder.Configuration.GetValue<int?>("Http:RestPort") ?? 6001;
var grpcPort = builder.Configuration.GetValue<int?>("Http:GrpcPort") ?? 6002;
var serviceName = builder.Configuration["Observability:ServiceName"] ?? "Api.Net10";
var otlpEndpoint = builder.Configuration["Observability:OtlpEndpoint"] ?? "http://localhost:18890";
var otlpProtocol = builder.Configuration["Observability:OtlpProtocol"] ?? "http/protobuf";
var systemName = builder.Configuration["Observability:SystemName"] ?? "OnlineSalesMotorSE";
var systemCode = builder.Configuration["Observability:SystemCode"] ?? "MOTOR";
var deploymentEnvironment = builder.Configuration["Observability:DeploymentEnvironment"] ?? "local";
var metricInterval = builder.Configuration.GetValue<int?>("Observability:MetricExportIntervalMilliseconds") ?? 5000;
var enableConsoleMetrics = builder.Configuration.GetValue<bool?>("Observability:EnableConsoleMetrics") ?? false;
var enableGcCounters = builder.Configuration.GetValue<bool?>("Diagnostics:EnableGcCounters") ?? false;
if (metricInterval <= 0)
{
    metricInterval = 5000;
}

// Configure separate ports to avoid HTTP/1.1 and HTTP/2 conflicts.
// REST uses HTTP/1.1 on the configured REST port, gRPC uses HTTP/2 on the configured gRPC port.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(restPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
    options.ListenLocalhost(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

// gRPC services require HTTP/2 and explicit size limits for large payloads.
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 50 * 1024 * 1024;
    options.MaxSendMessageSize = 50 * 1024 * 1024;
});

// OpenTelemetry setup via hosting extensions.
// Best practices:
// https://opentelemetry.io/docs/specs/otel/
// https://opentelemetry.io/docs/specs/semconv/
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        // Resource attributes applied to all signals.
        resource.AddService(serviceName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["system.name"] = systemName,
                ["system.code"] = systemCode,
                ["deployment.environment"] = deploymentEnvironment,
            });
    })
    .WithTracing(tracing =>
    {
        // Tracing: ASP.NET Core incoming requests + custom ActivitySource.
        tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(Observability.ActivitySourceName)
            .AddOtlpExporter(options =>
            {
                // OTLP/HTTP-Protobuf -> Aspire dashboard.
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = string.Equals(otlpProtocol, "grpc", StringComparison.OrdinalIgnoreCase)
                    ? OtlpExportProtocol.Grpc
                    : OtlpExportProtocol.HttpProtobuf;
            });
    })
    .WithMetrics(metrics =>
    {
        // Metrics: ASP.NET Core request metrics + custom Meter.
        metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(Observability.MeterName);

        // Periodic metric reader with explicit export interval for short-lived processes.
        var metricExporter = new OpenTelemetry.Exporter.OtlpMetricExporter(new OpenTelemetry.Exporter.OtlpExporterOptions
        {
            Endpoint = new Uri(otlpEndpoint),
            Protocol = string.Equals(otlpProtocol, "grpc", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.Grpc
                : OtlpExportProtocol.HttpProtobuf,
        });
        metrics.AddReader(new PeriodicExportingMetricReader(
            metricExporter,
            exportIntervalMilliseconds: metricInterval));

        if (enableConsoleMetrics)
        {
            // Console exporter is a debugging aid to verify metrics are produced locally.
            metrics.AddConsoleExporter();
        }
    });

var app = builder.Build();

// GC/runtime counters are optional and can be enabled for diagnostics.
GcCounterListener gcListener = null;
if (enableGcCounters)
{
    gcListener = new GcCounterListener("Api.Net10", Console.WriteLine);
}
// REST endpoint for payload generation.
app.MapPost("/api/work", (WorkRequest request) =>
{
    // Enrich the current server span with request metadata.
    Activity.Current?.SetTag("correlationId", request.CorrelationId ?? string.Empty);
    Activity.Current?.SetTag("payload.size", request.PayloadSize);

    // Child span to isolate payload generation time.
    using var activity = Observability.ActivitySource.StartActivity("GeneratePayload", ActivityKind.Internal);
    activity?.SetTag("payload.size", request.PayloadSize);

    var payload = PayloadGenerator.Generate(request.PayloadSize);
    return Results.Ok(new WorkResponse
    {
        BigString = payload.BigString,
        Items = payload.Items,
        Metadata = payload.Metadata,
    });
});

// gRPC endpoint for payload generation (WorkService.GetWork).
app.MapGrpcService<WorkServiceImpl>();

// Root endpoint for quick sanity checks.
app.MapGet("/", () => "Api.Net10 running. Use /api/work or gRPC WorkService.GetWork.");

// Ensure telemetry is flushed when the host is stopping.
app.Lifetime.ApplicationStopping.Register(() =>
{
    (app.Services.GetService<OpenTelemetry.Metrics.MeterProvider>() as IDisposable)?.Dispose();
    (app.Services.GetService<OpenTelemetry.Trace.TracerProvider>() as IDisposable)?.Dispose();
    gcListener?.Dispose();
});

app.Run();
