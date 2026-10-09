using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using SharePointAgent.Infrastructure.GraphRag.Cosmos;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

/// <summary>The stored JSON shapes, which other tools, migrations, and the Cosmos queries depend on.</summary>
public sealed class GraphStorageMappingTests
{
    private static readonly JsonSerializerOptions CosmosJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static GraphAssertion SampleAssertion(GraphAssertionStatus status = GraphAssertionStatus.Asserted)
    {
        var record = File("item-1");
        return Assertion(Entity("System", "CRM"), "DEPENDS_ON", Entity("System", "ERP"), Evidence(record, 0, "CRM depends on ERP") with { PageNumber = 9 }, status);
    }

    [Fact]
    public void AssertionDocumentsMatchTheDocumentedShapeAndRoundTrip()
    {
        var assertion = SampleAssertion();
        var locator = new GraphPartitionLocator(16);
        var document = AssertionDocument.From(assertion, locator.For(Tenant, assertion.SubjectEntityId), DateTimeOffset.UnixEpoch);

        var json = JsonNode.Parse(JsonSerializer.Serialize(document, CosmosJson))!.AsObject();

        Assert.Equal(assertion.AssertionId, (string?)json["id"]);
        Assert.StartsWith($"{Tenant}|", (string?)json["partitionKey"]);
        Assert.Equal("asserted", (string?)json["status"]);
        Assert.Equal(9, (int?)json["evidence"]!["pageNumber"]);
        Assert.False(json.ContainsKey("ttl"));
        Assert.False(json["evidence"]!.AsObject().ContainsKey("section"));
        Assert.Equal(assertion, JsonSerializer.Deserialize<AssertionDocument>(json.ToJsonString(), CosmosJson)!.ToAssertion());
    }

    [Theory]
    [InlineData(GraphAssertionStatus.Disputed, "disputed")]
    [InlineData(GraphAssertionStatus.Retracted, "retracted")]
    public void StatusesAreStoredInLowerCase(GraphAssertionStatus status, string stored)
    {
        var document = AssertionDocument.From(SampleAssertion(status), "p", DateTimeOffset.UnixEpoch);

        Assert.Equal(stored, document.Status);
        Assert.Equal(status, document.ToAssertion().Status);
    }

    [Fact]
    public void DocumentStatesRoundTripAndCarryTheirTimeToLiveOnlyWhenSet()
    {
        var state = ReadyState(File("item-1")) with
        {
            Manifest = [new GraphManifestEntry("assertion:1", "system:a", "DEPENDS_ON", "system:b", "chunk")],
            AccessScopeRef = "perm:x"
        };

        var kept = JsonNode.Parse(JsonSerializer.Serialize(DocumentStateDocument.From(state, "doc-1", "p", null), CosmosJson))!.AsObject();
        var expiring = JsonNode.Parse(JsonSerializer.Serialize(DocumentStateDocument.From(state, "doc-1", "p", TimeSpan.FromDays(1)), CosmosJson))!.AsObject();

        Assert.False(kept.ContainsKey("ttl"));
        Assert.False(kept.ContainsKey("_etag"));
        Assert.Equal(86400, (int?)expiring["ttl"]);
        Assert.Equal("Ready", (string?)kept["status"]);
        var restored = JsonSerializer.Deserialize<DocumentStateDocument>(kept.ToJsonString(), CosmosJson)!.ToState();
        Assert.Equal(state.Manifest, restored.Manifest);
        Assert.True(restored.IsServing(state.ActiveRevision!));
    }

    [Fact]
    public void StateIdsAreOpaqueAndStable()
    {
        var id = CosmosGraphProjectionStore.StateId("b!drive:01ITEM");

        Assert.StartsWith("doc-", id);
        Assert.DoesNotContain("01ITEM", id);
        Assert.Equal(id, CosmosGraphProjectionStore.StateId("b!drive:01ITEM"));
        Assert.True(GraphIds.IsStorageSafe(id));
    }

    [Fact]
    public void ContainersIndexOnlyTheQueriedFields()
    {
        var assertions = CosmosGraphLayout.Assertions("graphAssertions");

        Assert.Equal("/partitionKey", assertions.PartitionKeyPath);
        Assert.Equal(-1, assertions.DefaultTimeToLive);
        Assert.Contains(assertions.IndexingPolicy.IncludedPaths, path => path.Path == "/subjectEntityId/?");
        Assert.Contains(assertions.IndexingPolicy.ExcludedPaths, path => path.Path == "/*");
        Assert.DoesNotContain(CosmosGraphLayout.Entities("graphEntities").IndexingPolicy.IncludedPaths, path => path.Path.StartsWith("/aliases"));
    }

    [Fact]
    public void ArchivedSnapshotsRoundTripExactly()
    {
        var record = File("item-1");
        var crm = Entity("System", "CRM") with { Aliases = ["Customer Relationship Management"], Attributes = new Dictionary<string, string> { ["vendor"] = "Contoso" } };
        var erp = Entity("System", "ERP");
        var snapshot = Snapshot(record, [crm, erp], [SampleAssertion(GraphAssertionStatus.Disputed)]);

        var json = JsonSerializer.Serialize(snapshot, BlobCanonicalGraphArchive.SerializerOptions);
        var restored = JsonSerializer.Deserialize<CanonicalGraphSnapshot>(json, BlobCanonicalGraphArchive.SerializerOptions)!;

        Assert.Equal(snapshot.Assertions, restored.Assertions);
        Assert.Equal(
            snapshot.Resolutions.Select(decision => (decision.LocalRef, decision.EntityId, decision.Method)),
            restored.Resolutions.Select(decision => (decision.LocalRef, decision.EntityId, decision.Method)));
        Assert.Equal(snapshot.Extraction, restored.Extraction);
        Assert.Equal("Contoso", restored.Entities[0].Attributes["vendor"]);
        Assert.True(new CanonicalGraphSnapshotValidator(Ontology).Validate(restored).IsValid);
        Assert.DoesNotContain("CRM depends on ERP", json);
    }

    [Fact]
    public void ArchiveBlobNamesAreVersionedAndHideDocumentIdentifiers()
    {
        var name = BlobCanonicalGraphArchive.BlobName(Tenant, "b!drive:01ITEM", "rev:abc", "graph-extraction-v1.v1.1234abcd");

        Assert.StartsWith($"v1/{Tenant}/", name);
        Assert.EndsWith("/rev-abc/graph-extraction-v1.v1.1234abcd.json", name);
        Assert.DoesNotContain("01ITEM", name);
    }
}
