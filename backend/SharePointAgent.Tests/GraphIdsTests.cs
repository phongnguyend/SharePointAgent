using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class GraphIdsTests
{
    private static readonly EvidenceRef Evidence = new(
        "tenant-a", "drive-1:item-1", "rev:1", SearchChunkKey.For("drive-1", "item-1", 0), GraphDocumentIds.ForChunkContent("text"), null, null, null);

    [Fact]
    public void EntityIdsAreDeterministicOpaqueAndPrefixedWithTheType()
    {
        var first = GraphIds.ForEntity("tenant-a", "System", GraphEntityKey.FromName("Customer Portal"));
        var second = GraphIds.ForEntity("tenant-a", "System", GraphEntityKey.FromName("  customer   PORTAL "));

        Assert.Equal(first, second);
        Assert.StartsWith("system:", first);
        Assert.DoesNotContain("portal", first, StringComparison.OrdinalIgnoreCase);
        Assert.True(GraphIds.IsStorageSafe(first));
    }

    [Fact]
    public void EntityIdsAreScopedToTheTenantAndType()
    {
        var key = GraphEntityKey.FromName("CRM");

        Assert.NotEqual(GraphIds.ForEntity("tenant-a", "System", key), GraphIds.ForEntity("tenant-b", "System", key));
        Assert.NotEqual(GraphIds.ForEntity("tenant-a", "System", key), GraphIds.ForEntity("tenant-a", "Project", key));
    }

    [Fact]
    public void NameKeysAndIdentifierKeysDoNotCollide()
    {
        Assert.NotEqual(GraphEntityKey.FromName("ci-42"), GraphEntityKey.FromIdentifier("cmdb", "ci-42"));
    }

    [Fact]
    public void NameNormalizationKeepsPunctuationSoDistinctNamesStayDistinct()
    {
        Assert.NotEqual(GraphNormalization.NormalizeName("C#"), GraphNormalization.NormalizeName("C++"));
        Assert.Equal("sap s/4hana", GraphNormalization.NormalizeName(" SAP\tS/4HANA "));
    }

    [Fact]
    public void NameNormalizationFoldsCompatibilityCharacters()
    {
        Assert.Equal(GraphNormalization.NormalizeName("CRM"), GraphNormalization.NormalizeName("ＣＲＭ"));
    }

    [Theory]
    [InlineData("depends on")]
    [InlineData("Depends-On")]
    [InlineData(" DEPENDS_ON ")]
    public void PredicatesNormalizeToUpperSnakeCase(string value)
    {
        Assert.Equal("DEPENDS_ON", GraphNormalization.NormalizePredicate(value));
    }

    [Fact]
    public void AssertionIdsAreDeterministicForEvidenceAndRelation()
    {
        var first = GraphIds.ForAssertion(Evidence, "system:a", "DEPENDS_ON", "system:b", symmetric: false);
        var second = GraphIds.ForAssertion(Evidence with { Section = "Overview", AccessScopeRef = "hash" }, "system:a", "DEPENDS_ON", "system:b", symmetric: false);

        Assert.Equal(first, second);
        Assert.StartsWith("assertion:", first);
        Assert.True(GraphIds.IsStorageSafe(first));
    }

    [Fact]
    public void AssertionIdsChangeWithTheDocumentRevisionChunkAndDirection()
    {
        var baseline = GraphIds.ForAssertion(Evidence, "system:a", "DEPENDS_ON", "system:b", symmetric: false);

        Assert.NotEqual(baseline, GraphIds.ForAssertion(Evidence with { DocumentVersion = "rev:2" }, "system:a", "DEPENDS_ON", "system:b", symmetric: false));
        Assert.NotEqual(baseline, GraphIds.ForAssertion(Evidence with { ChunkId = "other" }, "system:a", "DEPENDS_ON", "system:b", symmetric: false));
        Assert.NotEqual(baseline, GraphIds.ForAssertion(Evidence with { TenantId = "tenant-b" }, "system:a", "DEPENDS_ON", "system:b", symmetric: false));
        Assert.NotEqual(baseline, GraphIds.ForAssertion(Evidence, "system:b", "DEPENDS_ON", "system:a", symmetric: false));
    }

    [Fact]
    public void SymmetricAssertionIdsIgnoreDirection()
    {
        Assert.Equal(
            GraphIds.ForAssertion(Evidence, "system:a", "INTEGRATES_WITH", "system:b", symmetric: true),
            GraphIds.ForAssertion(Evidence, "system:b", "INTEGRATES_WITH", "system:a", symmetric: true));
    }

    [Fact]
    public void HashInputsAreLengthPrefixedSoFieldBoundariesCannotShift()
    {
        Assert.NotEqual(
            GraphIds.ForEntity("tenant-a", "System", "name:b"),
            GraphIds.ForEntity("tenant-aS", "ystem", "name:b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" padded")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a\nb")]
    public void UnsafeStorageKeysAreRejected(string? value)
    {
        Assert.False(GraphIds.IsStorageSafe(value));
    }

    [Fact]
    public void StorageKeysLongerThanTheCosmosLimitAreRejected()
    {
        Assert.True(GraphIds.IsStorageSafe(new string('a', GraphIds.MaxIdLength)));
        Assert.False(GraphIds.IsStorageSafe(new string('a', GraphIds.MaxIdLength + 1)));
    }

    [Fact]
    public void DocumentIdsRoundTripToTheDriveItem()
    {
        var documentId = GraphDocumentIds.ForDriveItem("b!drive", "01ITEM");

        Assert.True(GraphDocumentIds.TryParseDriveItem(documentId, out var driveId, out var itemId));
        Assert.Equal("b!drive", driveId);
        Assert.Equal("01ITEM", itemId);
        Assert.False(GraphDocumentIds.TryParseDriveItem("a:b:c", out _, out _));
        Assert.Throws<ArgumentException>(() => GraphDocumentIds.ForDriveItem("a:b", "c"));
    }

    [Fact]
    public void ChunkIdsAreTheExistingSearchChunkKeys()
    {
        Assert.Equal(SearchChunkKey.For("drive", "item", 3), GraphDocumentIds.ForChunk("drive", "item", 3));
    }

    [Fact]
    public void ChunkContentHashesIdentifyTheExactText()
    {
        var hash = GraphDocumentIds.ForChunkContent("CRM depends on ERP.");

        Assert.True(GraphDocumentIds.IsChunkContentHash(hash));
        Assert.Equal(hash, GraphDocumentIds.ForChunkContent("CRM depends on ERP."));
        Assert.NotEqual(hash, GraphDocumentIds.ForChunkContent("CRM depends on ERP"));
        Assert.DoesNotContain("CRM", hash);
        Assert.False(GraphDocumentIds.IsChunkContentHash("sha256:XYZ"));
        Assert.False(GraphDocumentIds.IsChunkContentHash(hash.ToUpperInvariant()));
    }

    [Fact]
    public void IndexedFileRevisionsFallBackToTheEntityTagAndRequireAFingerprint()
    {
        Assert.Equal(GraphDocumentIds.ForRevision("c", "f"), GraphDocumentIds.ForIndexedFile("c", "e", "f"));
        Assert.Equal(GraphDocumentIds.ForRevision("e", "f"), GraphDocumentIds.ForIndexedFile(null, "e", "f"));
        Assert.Null(GraphDocumentIds.ForIndexedFile(null, null, "f"));
        Assert.Null(GraphDocumentIds.ForIndexedFile("c", "e", ""));
    }

    [Fact]
    public void AccessScopeReferencesAreStorageSafe()
    {
        var reference = GraphDocumentIds.ForAccessScope("ab+/cd==");

        Assert.Equal("perm:ab-_cd", reference);
        Assert.True(GraphIds.IsStorageSafe(reference));
        Assert.Null(GraphDocumentIds.ForAccessScope(null));
    }

    [Fact]
    public void ExtractionVersionsChangeWithPromptModelOrOntology()
    {
        var version = GraphExtractionVersion.Compute("p1", "gpt-a", "v1");

        Assert.StartsWith("p1.v1.", version);
        Assert.Equal(version, GraphExtractionVersion.Compute("p1", "gpt-a", "v1"));
        Assert.NotEqual(version, GraphExtractionVersion.Compute("p1", "gpt-b", "v1"));
        Assert.NotEqual(version, GraphExtractionVersion.Compute("p2", "gpt-a", "v1"));
        Assert.NotEqual(version, GraphExtractionVersion.Compute("p1", "gpt-a", "v2"));
    }

    [Fact]
    public void PartitionKeysAreTenantScopedDeterministicAndSpreadOverBuckets()
    {
        var locator = new GraphPartitionLocator(16);
        var keys = Enumerable.Range(0, 500).Select(index => locator.For("tenant-a", $"system:{index}")).ToList();

        Assert.All(keys, key => Assert.Matches("^tenant-a\\|00(0[0-9]|1[0-5])$", key));
        Assert.Equal(16, keys.Distinct().Count());
        Assert.Equal(locator.For("tenant-a", "system:1"), new GraphPartitionLocator(16).For("tenant-a", "system:1"));
        Assert.NotEqual(locator.For("tenant-a", "system:1"), locator.For("tenant-b", "system:1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphPartitionLocator(0));
    }

    [Fact]
    public void RevisionsChangeWithContentOrChunkingSettings()
    {
        var revision = GraphDocumentIds.ForRevision("\"c:{A},3\"", "chunk=4000/400;embedding=e;dimensions=1536");

        Assert.Equal(revision, GraphDocumentIds.ForRevision("\"c:{A},3\"", "chunk=4000/400;embedding=e;dimensions=1536"));
        Assert.NotEqual(revision, GraphDocumentIds.ForRevision("\"c:{A},4\"", "chunk=4000/400;embedding=e;dimensions=1536"));
        Assert.NotEqual(revision, GraphDocumentIds.ForRevision("\"c:{A},3\"", "chunk=2000/200;embedding=e;dimensions=1536"));
        Assert.True(GraphIds.IsStorageSafe(revision));
    }
}
