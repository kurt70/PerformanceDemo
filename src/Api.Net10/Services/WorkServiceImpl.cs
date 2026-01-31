using System.Diagnostics;
using System.Threading.Tasks;
using Grpc.Core;
using PerformanceDemo.Proto;
using Shared.Observability;

namespace Api.Net10.Services
{
    // gRPC service implementation for WorkService.
    public sealed class WorkServiceImpl : WorkService.WorkServiceBase
    {
        public override Task<WorkResponse> GetWork(WorkRequest request, ServerCallContext context)
        {
            // Enrich the current server span with request metadata.
            Activity.Current?.SetTag("correlationId", request.CorrelationId ?? string.Empty);
            Activity.Current?.SetTag("payload.size", request.PayloadSize);

            // Child span to isolate payload generation time.
            using var activity = Observability.ActivitySource.StartActivity("GeneratePayload", ActivityKind.Internal);
            activity?.SetTag("payload.size", request.PayloadSize);

            var payload = PayloadGenerator.Generate(request.PayloadSize);
            var response = new WorkResponse
            {
                BigString = payload.BigString,
            };

            response.Items.AddRange(payload.Items);
            response.Metadata.Add(payload.Metadata);

            return Task.FromResult(response);
        }
    }
}
