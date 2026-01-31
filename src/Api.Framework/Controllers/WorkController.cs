using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Http;
using Api.Framework.Models;
using Shared.Observability;

namespace Api.Framework.Controllers
{
    // REST endpoint that generates a synthetic payload for perf testing.
    public sealed class WorkController : ApiController
    {
        [HttpPost]
        [Route("api/work")]
        public WorkResponse Post([FromBody] WorkRequest request)
        {
            if (request == null)
            {
                throw new HttpResponseException(System.Net.HttpStatusCode.BadRequest);
            }

            // Enrich the current server span with request metadata.
            var current = Activity.Current;
            current?.SetTag("correlationId", request.CorrelationId ?? string.Empty);
            current?.SetTag("payload.size", request.PayloadSize);

            // Child span for payload generation.
            using (var activity = Observability.ActivitySource.StartActivity("GeneratePayload", ActivityKind.Internal))
            {
                activity?.SetTag("payload.size", request.PayloadSize);
                return GenerateResponse(request);
            }
        }

        // Deterministic payload generation to keep responses comparable across runs.
        private static WorkResponse GenerateResponse(WorkRequest request)
        {
            var payloadSize = Math.Max(0, request.PayloadSize);
            var bigString = new string('x', payloadSize);
            var itemCount = Math.Max(10, Math.Min(200, payloadSize / 32));
            var items = new List<string>(itemCount);
            for (var i = 0; i < itemCount; i++)
            {
                items.Add("item-" + i + "-size-" + payloadSize);
            }

            return new WorkResponse
            {
                BigString = bigString,
                Items = items,
                Metadata = new Dictionary<string, string>
                {
                    ["payloadSize"] = payloadSize.ToString(),
                    ["items"] = itemCount.ToString(),
                    ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                },
            };
        }
    }
}
