# PerformanceDemo

Reference repo for local performance tests (REST/JSON vs gRPC/Protobuf) and observability (OpenTelemetry -> Aspire Dashboard).

## Components
- Api.Framework (.NET Framework 4.8.1, OWIN self-host console, REST/JSON)
- Api.Net10 (.NET 10, REST/JSON + gRPC)
- Bff.Runner.Framework (.NET Framework 4.8.1 console load runner)
- Shared.Observability (shared OpenTelemetry setup)

## Aspire Dashboard (local visualization)
```powershell
docker compose up -d
```

## Start APIs
Api.Framework (build in Visual Studio 2022 first):
```powershell
Start-Process .\src\Api.Framework\bin\Debug\net481\Api.Framework.exe
```

Api.Net10:
```powershell
dotnet run --project .\src\Api.Net10\Api.Net10.csproj
```
Note: REST and gRPC ports are configured in appsettings.json.

## Run runner (3 scenarios)
```powershell
.\src\Bff.Runner.Framework\bin\Debug\net481\Bff.Runner.Framework.exe --iterations 500 --concurrency 20 --backend framework --protocol rest --payload 4096
.\src\Bff.Runner.Framework\bin\Debug\net481\Bff.Runner.Framework.exe --iterations 500 --concurrency 20 --backend net10 --protocol rest --payload 4096
.\src\Bff.Runner.Framework\bin\Debug\net481\Bff.Runner.Framework.exe --iterations 500 --concurrency 20 --backend net10 --protocol grpc --payload 4096
```

## Latest results
Date: January 31, 2026
Time: 2026-01-31 18:37
Duration: 300 seconds
Warmup: 60 seconds
Payload: 5000 bytes
Concurrency: 10
CPU: Intel(R) Core(TM) Ultra 7 165H
RAM: 63.4 GB
OS: Windows 10.0.22631

| Backend/Protocol | Total (ms) | RPS | Avg (ms) | p50 (ms) | p95 (ms) | p99 (ms) |
| --- | --- | --- | --- | --- | --- | --- |
| framework/rest | 300013 | 6909.19 | 1.44 | 0.77 | 3.92 | 6.55 |
| net10/rest | 300015 | 11103.94 | 0.89 | 0.54 | 2.28 | 5.54 |
| net10/grpc | 300001 | 16812.37 | 0.59 | 0.46 | 1.25 | 2.38 |

Dashboard: http://localhost:18888

## Configuration
- Api.Framework uses App.config for base address and observability settings.
- Api.Net10 uses appsettings.json for ports and observability settings.
- Bff.Runner.Framework uses App.config for observability settings.
Metrics debugging:
- Set Observability.OtlpProtocol to "grpc" to use OTLP gRPC on port 18889.
- Set Observability.EnableConsoleMetrics to true to print metrics to the console.
- Set Diagnostics.EnableGcCounters to true to print runtime GC counters.
