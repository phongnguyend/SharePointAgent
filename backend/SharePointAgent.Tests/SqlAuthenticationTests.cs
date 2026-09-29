using Microsoft.Data.SqlClient;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SqlAuthenticationTests
{
    [Fact]
    public void ManagedIdentityAuthenticationProviderIsAvailable()
    {
        var provider = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity);

        Assert.NotNull(provider);
        Assert.True(provider.IsSupported(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity));
    }
}
