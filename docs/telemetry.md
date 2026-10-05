# OpenTelemetry

API, Background, and AgentHost share telemetry configuration. Traces include
ASP.NET Core requests, outgoing HTTP requests, SQL Client operations, agent runs,
Azure SDK activities when emitted, and SharePoint sync/reindex operations.
Correlated application logs and runtime/HTTP/server/agent metrics are also exported.
Prompt and response payload capture is not enabled by this setup.

## Local: Aspire dashboard

Run the existing AppHost from the repository root:

```powershell
dotnet run --project backend/SharePointAgent.AspireAppHost --launch-profile http
```

Open the dashboard URL printed by Aspire. AppHost sets `Monitoring__OpenTelemetry__Exporter=Otlp`
and `Monitoring__OpenTelemetry__Environment=local` on all three hosts. Aspire supplies the OTLP
endpoint, protocol, authentication headers, and service resource names.

For individually launched hosts, you can use the standalone dashboard:

```powershell
docker run --rm --name aspire-dashboard -p 127.0.0.1:18888:18888 -p 127.0.0.1:4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:13.5
```

Open the login URL/token printed by the container. In each host's launch shell:

```powershell
$env:Monitoring__OpenTelemetry__Exporter = "Otlp"
$env:Monitoring__OpenTelemetry__Environment = "local"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:OTEL_EXPORTER_OTLP_PROTOCOL = "grpc"
dotnet run --project backend/SharePointAgent.Api
```

Use the Background or AgentHost project path for those processes. Existing
application settings and credentials are still required. For apps in containers,
use the dashboard's container-network hostname or `host.docker.internal`, rather
than `localhost`. OTLP authentication, when configured on the dashboard, is supplied
via `OTEL_EXPORTER_OTLP_HEADERS`.

Without `OTEL_EXPORTER_OTLP_ENDPOINT`, OTLP mode registers instrumentation but
does not export. Set `Monitoring__OpenTelemetry__Exporter=None` to disable this registration.
See [standalone Aspire dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone).

## Dev/test: Azure Monitor

`infra/main.bicep` provisions workspace-based Application Insights connected to
the environment's Log Analytics workspace. API, Background, and AgentHost use
the same Application Insights resource, with distinct service names.

Run **Deploy infrastructure** first, then release API, Background, and AgentHost.
The release scripts read the infrastructure output and supply:

```dotenv
Monitoring__OpenTelemetry__Exporter=AzureMonitor
Monitoring__OpenTelemetry__Environment=dev
APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=...;IngestionEndpoint=...
```

For test, the environment label is `test`. No new GitHub secret is needed: the
connection string comes from provisioned infrastructure. ASP.NET environment
remains `Production` for deployed services; exporter choice is explicit and does
not treat the local ASP.NET `Development` environment as the Azure dev environment.
An absent Azure Monitor connection string fails startup with a configuration error.

The [Azure Monitor exporter](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/monitor.opentelemetry.exporter-readme)
is configured for full sampling of new traces in dev/test. Local tracing also
samples new traces; parent sampling decisions can affect downstream collection.
`OTEL_SERVICE_NAME` can override a host's default resource name.

## Find a chat turn

Use **View trace ID** on the question or response and copy the ID. Both contain
the original request's W3C trace ID. In Aspire, find that trace in the Traces view.
In Application Insights Logs, query:

```kusto
union requests, dependencies, traces, exceptions
| where operation_Id == "PASTE_TRACE_ID"
| order by timestamp asc
```

When querying the Log Analytics workspace directly, use workspace table names:

```kusto
union AppRequests, AppDependencies, AppTraces, AppExceptions
| where OperationId == "PASTE_TRACE_ID"
| order by TimeGenerated asc
```

Outgoing HTTP instrumentation propagates W3C trace context to downstream services.
Those services must preserve that context and export their own spans to see their
internal work. The Python PageIndex and MarkItDown services currently appear as
outgoing dependencies; their internal operations are not instrumented by this
.NET setup. Historical traces and unsampled or expired telemetry cannot be
recovered from the stored ID alone.
