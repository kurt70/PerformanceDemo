using System;
using System.Configuration;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using PerformanceDemo.Proto;
using Shared.Observability;

namespace Bff.Runner.Framework
{
    // Console load runner that drives REST or gRPC requests and records latency metrics.
    internal static class Program
    {
        // Shared HttpClient instance for REST calls.
        private static readonly HttpClient HttpClient = new HttpClient();

        private static async Task<int> Main(string[] args)
        {
            var options = Options.Parse(args);
            if (options == null)
            {
                PrintUsage();
                return 1;
            }

            if (string.Equals(options.Backend, "framework", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(options.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("gRPC is only supported with backend=net10.");
                return 1;
            }

            // Read telemetry configuration from App.config.
            var serviceName = ConfigurationManager.AppSettings["Runner.ServiceName"] ?? "Bff.Runner.Framework";
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

            var otelOptions = new Observability.ObservabilityOptions
            {
                OtlpEndpoint = otlpEndpoint,
                OtlpProtocol = otlpProtocol,
                SystemName = systemName,
                SystemCode = systemCode,
                DeploymentEnvironment = deploymentEnvironment,
                MetricExportIntervalMilliseconds = metricInterval,
                EnableConsoleMetrics = enableConsoleMetrics,
            };

            var providers = Observability.Start(serviceName, otelOptions);
            using (providers.TracerProvider)
            using (providers.MeterProvider)
            {
                // GC/runtime counters are optional and can be enabled for diagnostics.
                GcCounterListener gcListener = null;
                if (enableGcCounters)
                {
                    gcListener = new GcCounterListener("Bff.Runner.Framework", Console.WriteLine);
                }

                // Custom histogram for latency measurements.
                // Metric naming guidance:
                // https://opentelemetry.io/docs/specs/semconv/metrics/
                var histogram = Observability.Meter.CreateHistogram<double>("bff.request_latency_ms", "ms");

                Channel grpcChannel = null;
                WorkService.WorkServiceClient grpcClient = null;
                if (string.Equals(options.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
                {
                    // gRPC channel uses a dedicated HTTP/2 port (6002).
                    grpcChannel = new Channel("localhost:6002", ChannelCredentials.Insecure, new[]
                    {
                        new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 50 * 1024 * 1024),
                        new ChannelOption(ChannelOptions.MaxSendMessageLength, 50 * 1024 * 1024),
                    });
                    grpcClient = new WorkService.WorkServiceClient(grpcChannel);
                }

                // Use a semaphore to cap concurrency.
            var semaphore = new SemaphoreSlim(options.Concurrency);
            var latencies = new ConcurrentBag<double>();
            var tasks = new List<Task>(options.Iterations);
            var totalStopwatch = Stopwatch.StartNew();

            if (options.DurationSeconds > 0)
            {
                // Warmup run to stabilize JIT and caches.
                if (options.WarmupSeconds > 0)
                {
                    await RunDurationAsync(options, grpcClient, histogram, options.WarmupSeconds, recordMetrics: false)
                        .ConfigureAwait(false);
                }

                var measured = await RunDurationAsync(options, grpcClient, histogram, options.DurationSeconds, recordMetrics: true)
                    .ConfigureAwait(false);

                PrintStats(options, measured.Latencies, measured.Duration, measured.Latencies.Length, isDuration: true);
            }
            else
            {
                for (var i = 0; i < options.Iterations; i++)
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var correlationId = Guid.NewGuid().ToString("N");
                            var sw = Stopwatch.StartNew();
                            await SendRequest(options, correlationId, grpcClient).ConfigureAwait(false);
                            sw.Stop();
                            var elapsedMs = sw.Elapsed.TotalMilliseconds;
                            latencies.Add(elapsedMs);
                            // Low-cardinality tags for metrics only (no correlationId in metrics).
                            histogram.Record(elapsedMs, new[]
                            {
                                new KeyValuePair<string, object>("backend", options.Backend),
                                new KeyValuePair<string, object>("protocol", options.Protocol),
                            });
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                totalStopwatch.Stop();

                PrintStats(options, latencies.ToArray(), totalStopwatch.Elapsed, options.Iterations, isDuration: false);
            }

                if (grpcChannel != null)
                {
                    await grpcChannel.ShutdownAsync().ConfigureAwait(false);
                }

                gcListener?.Dispose();
            }

            return 0;
        }

        private static async Task SendRequest(Options options, string correlationId, WorkService.WorkServiceClient grpcClient)
        {
            // Client span that represents the outbound call.
            using (var activity = Observability.ActivitySource.StartActivity("BffRequest", ActivityKind.Client))
            {
                activity?.SetTag("backend", options.Backend);
                activity?.SetTag("protocol", options.Protocol);
                // correlationId is only attached to traces, not metrics.
                activity?.SetTag("correlationId", correlationId);

                if (string.Equals(options.Protocol, "rest", StringComparison.OrdinalIgnoreCase))
                {
                    var url = string.Equals(options.Backend, "framework", StringComparison.OrdinalIgnoreCase)
                        ? "http://localhost:5001/api/work"
                        : "http://localhost:6001/api/work";

                    var payload = new
                    {
                        payloadSize = options.PayloadSize,
                        correlationId = correlationId,
                    };

                    var json = JsonConvert.SerializeObject(payload);
                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                    {
                        // Propagate trace context over HTTP headers.
                        // Context propagation spec:
                        // https://opentelemetry.io/docs/specs/otel/context/
                        InjectTraceContext(activity, request);
                        using (var response = await HttpClient.SendAsync(request).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    var request = new WorkRequest
                    {
                        PayloadSize = options.PayloadSize,
                        CorrelationId = correlationId,
                    };

                    var metadata = new Metadata();
                    // Propagate trace context over gRPC metadata.
                    InjectTraceContext(activity, metadata);

                    await grpcClient.GetWorkAsync(request, metadata).ResponseAsync.ConfigureAwait(false);
                }
            }
        }

        // Inject W3C trace context into HTTP headers.
        private static void InjectTraceContext(Activity activity, HttpRequestMessage request)
        {
            if (activity == null)
            {
                return;
            }

            Propagators.DefaultTextMapPropagator.Inject(
                new PropagationContext(activity.Context, Baggage.Current),
                request,
                (message, key, value) => message.Headers.TryAddWithoutValidation(key, value));
        }

        // Inject W3C trace context into gRPC metadata.
        private static void InjectTraceContext(Activity activity, Metadata metadata)
        {
            if (activity == null)
            {
                return;
            }

            Propagators.DefaultTextMapPropagator.Inject(
                new PropagationContext(activity.Context, Baggage.Current),
                metadata,
                (headers, key, value) => headers.Add(key, value));
        }

        private static void PrintStats(Options options, double[] latencies, TimeSpan total, int requestCount, bool isDuration)
        {
            if (latencies.Length == 0)
            {
                Console.WriteLine("No latencies recorded.");
                return;
            }

            // Percentiles are computed on sorted samples for a simple, stable summary.
            Array.Sort(latencies);
            var avg = latencies.Average();
            var p50 = Percentile(latencies, 0.50);
            var p95 = Percentile(latencies, 0.95);
            var p99 = Percentile(latencies, 0.99);
            var rps = requestCount / total.TotalSeconds;

            Console.WriteLine("Backend: {0} | Protocol: {1}", options.Backend, options.Protocol);
            if (isDuration)
            {
                Console.WriteLine("Duration: {0}s | Concurrency: {1} | Payload: {2}", options.DurationSeconds, options.Concurrency, options.PayloadSize);
                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine("Warmup: {0}s", options.WarmupSeconds);
                }
            }
            else
            {
                Console.WriteLine("Iterations: {0} | Concurrency: {1} | Payload: {2}", options.Iterations, options.Concurrency, options.PayloadSize);
            }
            Console.WriteLine("Total: {0:F0} ms | RPS: {1:F2}", total.TotalMilliseconds, rps);
            Console.WriteLine("Avg: {0:F2} ms | p50: {1:F2} ms | p95: {2:F2} ms | p99: {3:F2} ms", avg, p50, p95, p99);
        }

        private static async Task<DurationResult> RunDurationAsync(
            Options options,
            WorkService.WorkServiceClient grpcClient,
            Histogram<double> histogram,
            int durationSeconds,
            bool recordMetrics)
        {
            var latencies = new ConcurrentBag<double>();
            var totalRequests = 0L;
            using (var cts = new CancellationTokenSource())
            {
                var workers = new List<Task>(options.Concurrency);
                var swTotal = Stopwatch.StartNew();
                for (var i = 0; i < options.Concurrency; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var correlationId = Guid.NewGuid().ToString("N");
                            var sw = Stopwatch.StartNew();
                            await SendRequest(options, correlationId, grpcClient).ConfigureAwait(false);
                            sw.Stop();

                            if (recordMetrics)
                            {
                                var elapsedMs = sw.Elapsed.TotalMilliseconds;
                                latencies.Add(elapsedMs);
                                Interlocked.Increment(ref totalRequests);
                                histogram.Record(elapsedMs, new[]
                                {
                                    new KeyValuePair<string, object>("backend", options.Backend),
                                    new KeyValuePair<string, object>("protocol", options.Protocol),
                                });
                            }
                        }
                    }, cts.Token));
                }

                await Task.Delay(TimeSpan.FromSeconds(durationSeconds)).ConfigureAwait(false);
                cts.Cancel();
                await Task.WhenAll(workers).ConfigureAwait(false);
                swTotal.Stop();

                if (!recordMetrics)
                {
                    return new DurationResult
                    {
                        Latencies = Array.Empty<double>(),
                        Duration = swTotal.Elapsed,
                    };
                }

                return new DurationResult
                {
                    Latencies = latencies.ToArray(),
                    Duration = swTotal.Elapsed,
                };
            }
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            // Nearest-rank percentile for simplicity and determinism.
            var position = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            position = Math.Max(0, Math.Min(sorted.Length - 1, position));
            return sorted[position];
        }

        private static void PrintUsage()
        {
            // CLI usage instructions.
            Console.WriteLine("Usage:");
            Console.WriteLine("  Bff.Runner.Framework --iterations <n> --concurrency <n> --backend <framework|net10> --protocol <rest|grpc> --payload <bytes>");
            Console.WriteLine("  Bff.Runner.Framework --durationSeconds <n> --warmupSeconds <n> --concurrency <n> --backend <framework|net10> --protocol <rest|grpc> --payload <bytes>");
        }

        private sealed class Options
        {
            // Defaults tuned for local smoke tests.
            public int Iterations { get; set; } = 100;
            public int Concurrency { get; set; } = 10;
            public string Backend { get; set; } = "framework";
            public string Protocol { get; set; } = "rest";
            public int PayloadSize { get; set; } = 4096;
            public int DurationSeconds { get; set; } = 0;
            public int WarmupSeconds { get; set; } = 0;

            public static Options Parse(string[] args)
            {
                // Simple CLI parser without external dependencies.
                var options = new Options();
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    if (i + 1 >= args.Length)
                    {
                        return null;
                    }

                    var value = args[++i];
                    switch (arg)
                    {
                        case "--iterations":
                            if (!int.TryParse(value, out var iterations)) return null;
                            options.Iterations = iterations;
                            break;
                        case "--concurrency":
                            if (!int.TryParse(value, out var concurrency)) return null;
                            options.Concurrency = concurrency;
                            break;
                        case "--backend":
                            options.Backend = value;
                            break;
                        case "--protocol":
                            options.Protocol = value;
                            break;
                        case "--payload":
                            if (!int.TryParse(value, out var payload)) return null;
                            options.PayloadSize = payload;
                            break;
                        case "--durationSeconds":
                            if (!int.TryParse(value, out var durationSeconds)) return null;
                            options.DurationSeconds = durationSeconds;
                            break;
                        case "--warmupSeconds":
                            if (!int.TryParse(value, out var warmupSeconds)) return null;
                            options.WarmupSeconds = warmupSeconds;
                            break;
                        default:
                            return null;
                    }
                }

                if (options.Concurrency <= 0 || options.PayloadSize < 0)
                {
                    return null;
                }

                if (options.DurationSeconds > 0)
                {
                    if (options.DurationSeconds <= 0 || options.WarmupSeconds < 0)
                    {
                        return null;
                    }
                }
                else if (options.Iterations <= 0)
                {
                    return null;
                }

                return options;
            }
        }

        private sealed class DurationResult
        {
            public double[] Latencies { get; set; } = Array.Empty<double>();
            public TimeSpan Duration { get; set; }
        }
    }
}
