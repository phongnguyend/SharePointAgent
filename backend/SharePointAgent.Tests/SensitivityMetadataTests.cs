using Microsoft.EntityFrameworkCore;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SensitivityMetadataTests
{
    private const string LabelId = "2096f6a2-d2f7-48be-b329-b73aaa526e5d";
    private static readonly FileSensitivity Status = new(null, null, false, true, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("true", "Confidential", true)]
    [InlineData("1", null, true)]
    [InlineData("false", "Old label", false)]
    public void ReadsOnlyEnabledLabelsAndPreservesSourceEncryption(string enabled, string? name, bool labeled)
    {
        var properties = new Dictionary<string, string> { [$"MSIP_Label_{LabelId}_Enabled"] = enabled };
        if (name is not null)
        {
            properties[$"MSIP_Label_{LabelId}_Name"] = name;
        }

        var actual = SensitivityMetadata.Read(properties, "tenant", Status);
        Assert.Equal(labeled, actual.IsLabeled);
        Assert.Equal(labeled ? LabelId : null, actual.LabelId);
        Assert.Equal(labeled ? name : null, actual.LabelName);
        Assert.True(actual.IsEncrypted);
        Assert.Equal(Status.CheckedAtUtc, actual.CheckedAtUtc);
    }

    [Fact]
    public void PrefersConfiguredTenantAndDoesNotGuessAmongForeignLabels()
    {
        var other = Guid.NewGuid().ToString();
        var properties = new Dictionary<string, string>
        {
            [$"MSIP_Label_{LabelId}_Enabled"] = "true",
            [$"MSIP_Label_{LabelId}_SiteId"] = "tenant",
            [$"MSIP_Label_{other}_Enabled"] = "true",
            [$"MSIP_Label_{other}_SiteId"] = "other"
        };
        Assert.Equal(LabelId, SensitivityMetadata.Read(properties, "tenant", Status).LabelId);
        var ambiguous = SensitivityMetadata.Read(properties, "unknown", Status);
        Assert.Null(ambiguous.LabelId);
        Assert.True(ambiguous.IsLabeled);
    }

    [Fact]
    public void MigrationSnapshotMatchesCurrentModel()
    {
        using var context = new SharePointIndexDbContext(new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains(context.Database.GetMigrations(), migration => migration.EndsWith("_InitialCreate"));
    }
}
