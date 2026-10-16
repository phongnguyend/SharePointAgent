using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ServiceBusConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApiAndWorkerAlwaysRequireServiceBusConfiguration(bool api)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        if (api)
        {
            services.AddWebhookServices(configuration);
        }
        else
        {
            services.AddChangeProcessorServices(configuration);
        }
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ServiceBusClient));
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value);
        Assert.Contains(exception.Failures, failure => failure.Contains("ServiceBus:ConnectionString"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListenerSwitchRemainsIndependent(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Processor:ChangeSignalListenerEnabled"] = enabled.ToString()
        }).Build();
        Assert.Equal(enabled, configuration.IsChangeSignalListenerEnabled());
    }
}
