using Azure.Identity;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AzureCredentialTests
{
    [Fact]
    public void SharedAzureCredentialSupportsTheDefaultCredentialChain()
    {
        Assert.IsType<DefaultAzureCredential>(DependencyInjection.CreateDefaultAzureCredential());
    }
}
