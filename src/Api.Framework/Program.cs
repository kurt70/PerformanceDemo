using System;
using System.Configuration;
using Microsoft.Owin.Hosting;
using Shared.Observability;

namespace Api.Framework
{
    // .NET Framework 4.8.1 self-hosted Web API using OWIN.
    internal static class Program
    {
        private static void Main(string[] args)
        {
            // Read configuration from App.config for base address and telemetry settings.
            var baseAddress = ConfigurationManager.AppSettings["ApiFramework.BaseAddress"] ?? "http://localhost:5001";
            var serviceName = ConfigurationManager.AppSettings["ApiFramework.ServiceName"] ?? "Api.Framework";
            var otlpEndpoint = ConfigurationManager.AppSettings["Observability.OtlpEndpoint"] ?? "http://localhost:18890";
            var otlpProtocol = ConfigurationManager.AppSettings["Observability.OtlpProtocol"] ?? "http/protobuf";
            var systemName = ConfigurationManager.AppSettings["Observability.SystemName"] ?? "OnlineSalesMotorSE";
            var systemCode = ConfigurationManager.AppSettings["Observability.SystemCode"] ?? "MOTOR";
            var deploymentEnvironment = ConfigurationManager.AppSettings["Observability.DeploymentEnvironment"] ?? "local";
            var metricIntervalRaw = ConfigurationManager.AppSettings["Observability.MetricExportIntervalMilliseconds"];
            var enableConsoleMetricsRaw = ConfigurationManager.AppSettings["Observability.EnableConsoleMetrics"];
            var enableGcCountersRaw = ConfigurationManager.AppSettings["Diagnostics.EnableGcCounters"];
            var metricInterval = 5000;
            if (!string.IsNullOrWhiteSpace(metricIntervalRaw))
            {
                int.TryParse(metricIntervalRaw, out metricInterval);
                if (metricInterval <= 0) metricInterval = 5000;
            }

            var enableConsoleMetrics = string.Equals(enableConsoleMetricsRaw, "true", StringComparison.OrdinalIgnoreCase);
            var enableGcCounters = string.Equals(enableGcCountersRaw, "true", StringComparison.OrdinalIgnoreCase);

            var options = new Observability.ObservabilityOptions
            {
                OtlpEndpoint = otlpEndpoint,
                OtlpProtocol = otlpProtocol,
                SystemName = systemName,
                SystemCode = systemCode,
                DeploymentEnvironment = deploymentEnvironment,
                MetricExportIntervalMilliseconds = metricInterval,
                EnableConsoleMetrics = enableConsoleMetrics,
            };

            // Start OpenTelemetry providers for traces and metrics.
            var providers = Observability.Start(serviceName, options);

            // GC/runtime counters are optional and can be enabled for diagnostics.
            GcCounterListener gcListener = null;
            if (enableGcCounters)
            {
                gcListener = new GcCounterListener("Api.Framework", Console.WriteLine);
            }

            using (providers.TracerProvider)
            using (providers.MeterProvider)
            using (WebApp.Start<Startup>(baseAddress))
            {
                // Simple console instructions for local use.
                Console.WriteLine("Api.Framework running at {0}", baseAddress);
                Console.WriteLine("POST {0}/api/work", baseAddress);
                Console.WriteLine("Press Enter to stop.");
                Console.ReadLine();
            }

            gcListener?.Dispose();

        }
    }
}
