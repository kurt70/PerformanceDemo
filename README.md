# PerformanceDemo

Referanse-/demo-repo for lokale ytelsestester (REST/JSON vs gRPC/Protobuf) og observability (OpenTelemetry -> Aspire Dashboard).

## Komponenter (plan)
- Api.Framework (NET Framework 4.8.1, OWIN self-host console, REST/JSON)
- Api.Net10 (NET 10, REST/JSON + gRPC)
- Bff.Runner.Framework (NET Framework 4.8.1 console load runner)
- Shared.Observability (felles OpenTelemetry-oppsett)

## Aspire Dashboard (lokal visualisering)
Start dashboard (OTLP endpoint + UI):
```bash
docker compose up -d
