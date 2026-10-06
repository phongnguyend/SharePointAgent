using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SharePointAgent.Infrastructure.Monitoring;

public static class Telemetry
{
    public const string SourceName = "SharePointAgent";

    public static readonly ActivitySource Activities = new(SourceName);

    public static T AddApplicationTelemetry<T>(this T builder, string serviceName) where T : IHostApplicationBuilder
    {
        var exporter = builder.Configuration["Monitoring:OpenTelemetry:Exporter"] ?? "Otlp";
        if (exporter.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return builder;
        }
        var azure = exporter.Equals("AzureMonitor", StringComparison.OrdinalIgnoreCase);
        if (!azure && !exporter.Equals("Otlp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Monitoring:OpenTelemetry:Exporter must be Otlp, AzureMonitor, or None.");
        }
        var connectionString = builder.Configuration["Monitoring:OpenTelemetry:AzureMonitor:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        }
        if (azure && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Monitoring:OpenTelemetry:AzureMonitor:ConnectionString or APPLICATIONINSIGHTS_CONNECTION_STRING is required for AzureMonitor telemetry.");
        }

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? serviceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = builder.Configuration["Monitoring:OpenTelemetry:Environment"] ?? builder.Environment.EnvironmentName
                }))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
                .AddSource(SourceName, "Azure.*", "Microsoft.Extensions.AI*", "Microsoft.Agents.AI*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation())
            .WithMetrics(metrics => metrics.AddMeter(
                "System.Runtime", "System.Net.Http", "Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.Extensions.AI*", SourceName));

        if (azure)
        {
            telemetry.UseAzureMonitorExporter(options =>
            {
                options.ConnectionString = connectionString;
                options.TracesPerSecond = null;
                options.SamplingRatio = 1.0f;
            });
        }
        else if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            // Aspire injects the endpoint, protocol and authentication headers.
            telemetry.UseOtlpExporter();
        }
        return builder;
    }
}
