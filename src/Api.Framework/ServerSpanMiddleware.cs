using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Owin;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Shared.Observability;

namespace Api.Framework
{
    // Manual server span middleware for OWIN.
    // This is useful when automatic instrumentation is not available.
    // Trace context propagation:
    // https://opentelemetry.io/docs/specs/otel/trace/api/
    public sealed class ServerSpanMiddleware : OwinMiddleware
    {
        // Global propagator used to extract incoming trace context from headers.
        private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;
        // Manual metrics because OWIN does not provide built-in metrics instrumentation.
        private static readonly Histogram<double> RequestLatencyMs =
            Observability.Meter.CreateHistogram<double>("api.request_latency_ms", "ms");
        private static readonly Counter<long> RequestCount =
            Observability.Meter.CreateCounter<long>("api.request_count");

        public ServerSpanMiddleware(OwinMiddleware next)
            : base(next)
        {
        }

        public override async Task Invoke(IOwinContext context)
        {
            // Measure end-to-end request latency.
            var sw = Stopwatch.StartNew();
            // Extract incoming trace context so the server span joins the distributed trace.
            var parentContext = Propagator.Extract(default, context.Request.Headers, ExtractValues);
            Baggage.Current = parentContext.Baggage;

            // Create a server span manually to represent the HTTP request.
            using (var activity = Observability.ActivitySource.StartActivity(
                "HTTP " + context.Request.Method,
                ActivityKind.Server,
                parentContext.ActivityContext))
            {
                if (activity != null)
                {
                    // Add basic HTTP semantic tags.
                    activity.SetTag("http.method", context.Request.Method);
                    activity.SetTag("http.target", context.Request.Path.Value);
                    activity.SetTag("http.url", context.Request.Uri.ToString());
                }

                await Next.Invoke(context);

                if (activity != null)
                {
                    // Capture the status code after downstream processing.
                    activity.SetTag("http.status_code", context.Response.StatusCode);
                }
            }

            sw.Stop();
            var tags = new[]
            {
                new KeyValuePair<string, object>("method", context.Request.Method),
                new KeyValuePair<string, object>("status_code", context.Response.StatusCode),
            };
            RequestLatencyMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            RequestCount.Add(1, tags);
        }

        // Helper for propagator header extraction.
        private static IEnumerable<string> ExtractValues(IHeaderDictionary headers, string name)
        {
            return headers.TryGetValue(name, out var values) ? values : Enumerable.Empty<string>();
        }
    }
}
