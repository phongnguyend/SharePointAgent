using SharePointAgent.Background;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.Monitoring;

var builder = Host.CreateApplicationBuilder(args);
builder.AddApplicationTelemetry("sharepointagent-background");
builder.Services.AddChangeProcessorServices(builder.Configuration);
builder.Services.AddOllayaClient(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WorkerHealthState>();
builder.Services.AddHostedService<WorkerHeartbeatService>();
builder.Services.AddHostedService<SubscriptionRenewalBackgroundService>();
if (builder.Configuration.IsChangeSignalListenerEnabled())
{
    builder.Services.AddHostedService<ChangeSignalListenerBackgroundService>();
}
builder.Services.AddHostedService<ScheduledSyncBackgroundService>();
builder.Services.AddHostedService<MarkItDownHealthBackgroundService>();

await builder.Build().RunAsync();
