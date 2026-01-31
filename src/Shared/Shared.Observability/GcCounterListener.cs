using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace Shared.Observability
{
    // EventCounters listener for GC/runtime diagnostics.
    // Docs: https://learn.microsoft.com/dotnet/core/diagnostics/event-counters
    public sealed class GcCounterListener : EventListener
    {
        private readonly Action<string> _log;
        private readonly string _prefix;
        private readonly HashSet<string> _enabledCounters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gen-0-gc-count",
            "gen-1-gc-count",
            "gen-2-gc-count",
            "gc-heap-size",
            "time-in-gc",
            "alloc-rate",
            "cpu-usage",
            "threadpool-queue-length",
            "threadpool-thread-count",
        };

        public GcCounterListener(string prefix, Action<string> log)
        {
            _prefix = prefix ?? "GC";
            _log = log ?? (_ => { });
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource == null)
            {
                return;
            }

            // "System.Runtime" provides runtime and GC counters.
            if (string.Equals(eventSource.Name, "System.Runtime", StringComparison.OrdinalIgnoreCase))
            {
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.None, new Dictionary<string, string>
                {
                    ["EventCounterIntervalSec"] = "5",
                });
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData?.Payload == null || eventData.Payload.Count == 0)
            {
                return;
            }

            if (!(eventData.Payload[0] is IDictionary<string, object> payload))
            {
                return;
            }

            if (!payload.TryGetValue("Name", out var nameObj) || nameObj == null)
            {
                return;
            }

            var name = nameObj.ToString();
            if (!_enabledCounters.Contains(name))
            {
                return;
            }

            var value = ReadValue(payload);
            if (value == null)
            {
                return;
            }

            _log($"{_prefix} {name}: {value}");
        }

        private static object ReadValue(IDictionary<string, object> payload)
        {
            if (payload.TryGetValue("Mean", out var mean))
            {
                return mean;
            }

            if (payload.TryGetValue("Increment", out var increment))
            {
                return increment;
            }

            if (payload.TryGetValue("Value", out var value))
            {
                return value;
            }

            return null;
        }
    }
}
