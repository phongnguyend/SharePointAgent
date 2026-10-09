using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class GraphEvidenceAuthorizationTests
{
    private readonly InMemoryFileMetadata _metadata = new();
    private readonly FakeAuthorizedChunkStore _chunks = new();
    private readonly FileIndexRecord _record = File("item-1");

    private GraphEvidenceVerifier Verifier() => new(_chunks, _metadata, Options());

    private GraphAssertion IndexedAssertion(string content = "CRM depends on ERP.", FileIndexRecord? record = null)
    {
        record ??= _record;
        _metadata.Put(record);
        _chunks.Put(record.DriveId, record.ItemId, 0, content);
        return Assertion(Entity("System", "CRM"), "DEPENDS_ON", Entity("System", "ERP"), Evidence(record, 0, content));
    }

    private static GraphTraversalEdge Edge(string from, string to, int depth, string id) =>
        new(new GraphAssertion(Tenant, id, from, "DEPENDS_ON", to, new EvidenceRef(Tenant, "d:i", "r", "c", "h", null, null, null), GraphAssertionStatus.Asserted, "v1", "x1"), depth, from, to);

    [Fact]
    public async Task AuthorizedCurrentEvidenceIsAccepted()
    {
        var assertion = IndexedAssertion();
        _chunks.Grant("user-a", Drive, "item-1", 0);

        var result = await Verifier().VerifyAsync(Tenant, "user-a", [assertion], default);

        Assert.Contains(assertion.AssertionId, result.AuthorizedAssertionIds);
        Assert.Equal("CRM depends on ERP.", Assert.Single(result.Chunks).Value.Content);
    }

    [Fact]
    public async Task EvidenceTheUserCannotReadIsRejected()
    {
        var assertion = IndexedAssertion();

        var result = await Verifier().VerifyAsync(Tenant, "user-a", [assertion], default);

        Assert.Empty(result.AuthorizedAssertionIds);
        Assert.Empty(result.Chunks);
        Assert.Equal(1, result.RejectedCount);
    }

    [Fact]
    public async Task EvidenceFromARevisionThatIsNoLongerIndexedIsRejected()
    {
        var assertion = IndexedAssertion();
        _chunks.Grant("user-a", Drive, "item-1", 0);
        _metadata.Put(File("item-1", "\"c:{1},9\""));

        var result = await Verifier().VerifyAsync(Tenant, "user-a", [assertion], default);

        Assert.Empty(result.AuthorizedAssertionIds);
    }

    [Fact]
    public async Task EvidenceFromADeletedDocumentIsRejected()
    {
        var assertion = IndexedAssertion();
        _chunks.Grant("user-a", Drive, "item-1", 0);
        _metadata.Remove(Drive, "item-1");

        Assert.Empty((await Verifier().VerifyAsync(Tenant, "user-a", [assertion], default)).AuthorizedAssertionIds);
    }

    [Fact]
    public async Task EvidenceWhoseChunkNowHoldsOtherTextIsRejected()
    {
        // The index has been rewritten under the same chunk key, but the indexed-file table still names the
        // old revision: only the content hash can tell.
        var assertion = IndexedAssertion();
        _chunks.Put(Drive, "item-1", 0, "CRM no longer depends on ERP.");
        _chunks.Grant("user-a", Drive, "item-1", 0);

        Assert.Empty((await Verifier().VerifyAsync(Tenant, "user-a", [assertion], default)).AuthorizedAssertionIds);
    }

    [Fact]
    public async Task EvidenceFromAnotherTenantIsRejectedWithoutReadingIt()
    {
        var assertion = IndexedAssertion();
        _chunks.Grant("user-a", Drive, "item-1", 0);

        var result = await Verifier().VerifyAsync(Tenant, "user-a", [assertion with { TenantId = "tenant-b" }, assertion with { AssertionId = "assertion:x", Evidence = assertion.Evidence with { TenantId = "tenant-b" } }], default);

        Assert.Empty(result.AuthorizedAssertionIds);
        Assert.Equal(2, result.RejectedCount);
    }

    [Fact]
    public async Task EvidenceIsNeverVerifiedWithoutAUser()
    {
        var assertion = IndexedAssertion();

        await Assert.ThrowsAsync<ArgumentException>(() => Verifier().VerifyAsync(Tenant, " ", [assertion], default));
        Assert.Equal(0, _chunks.Calls);
    }

    [Fact]
    public void ASecondHopBehindAnUnauthorizedFirstHopIsDropped()
    {
        var edges = new[] { Edge("crm", "erp", 1, "a1"), Edge("erp", "billing", 2, "a2") };

        var usable = GraphPathAuthorization.SelectReachableEdges(["crm"], edges, new HashSet<string> { "a2" });

        Assert.Empty(usable);
    }

    [Fact]
    public void AnAlternativeFullyAuthorizedPathKeepsAnEdge()
    {
        var edges = new[]
        {
            Edge("crm", "erp", 1, "hidden"),
            Edge("crm", "portal", 1, "visible"),
            Edge("portal", "erp", 2, "bridge"),
            Edge("erp", "billing", 2, "second")
        };

        var usable = GraphPathAuthorization.SelectReachableEdges(["crm"], edges, new HashSet<string> { "visible", "bridge", "second" });

        Assert.Equal(["visible", "bridge", "second"], usable.Select(edge => edge.Assertion.AssertionId).ToArray());
    }
}
