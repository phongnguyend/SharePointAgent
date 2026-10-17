using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SharePointAgent.Infrastructure.Monitoring;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class TelemetryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(";IngestionEndpoint=https://example.com/")]
    public async Task AgentHostTelemetryPreservesApplicationConnectionString(string? platformConnectionString)
    {
        const string connectionString = "InstrumentationKey=11111111-1111-1111-1111-111111111111";
        var builder = Azure.AI.AgentServer.Core.AgentHost.CreateBuilder([]);
        builder.Configuration["Monitoring:OpenTelemetry:Exporter"] = "AzureMonitor";
        builder.Configuration["Monitoring:OpenTelemetry:AzureMonitor:ConnectionString"] = connectionString;
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = platformConnectionString;
        builder.WebApplicationBuilder.AddApplicationTelemetry("test-agent");
        var agent = builder.Build();
        await using var app = agent.App;
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Azure.Monitor.OpenTelemetry.Exporter.AzureMonitorExporterOptions>>().CurrentValue;
        Assert.Equal(connectionString, options.ConnectionString);
        Assert.Null(options.TracesPerSecond);
        Assert.Equal(1.0f, options.SamplingRatio);
        Assert.NotNull(app.Services.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>());
    }

    [Fact]
    public void LocalTelemetryPreservesTraceContextAndServiceIdentity()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monitoring:OpenTelemetry:Exporter"] = "Otlp",
            ["Monitoring:OpenTelemetry:Environment"] = "local"
        });
        builder.AddApplicationTelemetry("test-api");
        var exporter = new CaptureExporter();
        builder.Services.AddOpenTelemetry().WithTracing(tracing =>
            tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<TracerProvider>();
        Assert.Contains(provider.GetResource().Attributes, attribute => attribute.Key == "service.name" && Equals(attribute.Value, "test-api"));
        Assert.Contains(provider.GetResource().Attributes, attribute => attribute.Key == "deployment.environment.name" && Equals(attribute.Value, "local"));
        var parent = ActivityContext.Parse("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", null);
        using (var activity = Telemetry.Activities.StartActivity("test-request", ActivityKind.Server, parent))
        {
            Assert.NotNull(activity);
            Assert.Equal(parent.TraceId, activity.TraceId);
        }
        Assert.Contains(exporter.Activities, activity => activity.OperationName == "test-request" && activity.TraceId == parent.TraceId);
    }

    [Theory]
    [InlineData("AzureMonitor", "APPLICATIONINSIGHTS_CONNECTION_STRING")]
    [InlineData("invalid", "Monitoring:OpenTelemetry:Exporter")]
    public void InvalidExporterConfigurationFailsClearly(string exporter, string message)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Monitoring:OpenTelemetry:Exporter"] = exporter;
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => builder.AddApplicationTelemetry("test")).Message);
    }

    [Fact]
    public void DisabledTelemetryDoesNotRegisterProvider()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Monitoring:OpenTelemetry:Exporter"] = "None";
        builder.AddApplicationTelemetry("test");
        using var host = builder.Build();
        Assert.Null(host.Services.GetService<TracerProvider>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("InstrumentationKey=11111111-1111-1111-1111-111111111111")]
    public void AzureMonitorSupportsApplicationSettingAndLegacyFallback(string? applicationSetting)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Monitoring:OpenTelemetry:Exporter"] = "AzureMonitor";
        builder.Configuration["Monitoring:OpenTelemetry:AzureMonitor:ConnectionString"] = applicationSetting;
        // The application setting must take precedence over even an invalid platform value.
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.IsNullOrWhiteSpace(applicationSetting)
            ? "InstrumentationKey=22222222-2222-2222-2222-222222222222"
            : "invalid-platform-value";
        builder.AddApplicationTelemetry("test-agent");
        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
    }

    [Fact]
    public void AzureMonitorAcceptsApplicationSettingWithoutPlatformVariable()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Monitoring:OpenTelemetry:Exporter"] = "AzureMonitor";
        builder.Configuration["Monitoring:OpenTelemetry:AzureMonitor:ConnectionString"] = "InstrumentationKey=11111111-1111-1111-1111-111111111111";
        builder.AddApplicationTelemetry("test-agent");
        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
    }

    private sealed class CaptureExporter : BaseExporter<Activity>
    {
        public List<Activity> Activities { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                Activities.Add(activity);
            }
            return ExportResult.Success;
        }
    }
}
