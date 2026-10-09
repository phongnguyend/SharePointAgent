using Microsoft.Extensions.Logging.Abstractions;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class BoundedGraphTraversalTests
{
    private static readonly HashSet<string> DependsOn = ["DEPENDS_ON"];

    private readonly InMemoryGraphProjectionStore _store = new();
    private readonly FileIndexRecord _record = File("item-1");

    private BoundedGraphTraversal Traversal() => new(_store, Ontology, NullLogger<BoundedGraphTraversal>.Instance);

    public BoundedGraphTraversalTests()
    {
        _store.SeedState(ReadyState(_record));
    }

    private GraphAssertion Edge(string from, string to, string predicate = "DEPENDS_ON", int chunk = 0, FileIndexRecord? record = null)
    {
        var assertion = Assertion(Entity("System", from), predicate, Entity("System", to), Evidence(record ?? _record, chunk, $"{from} {predicate} {to}"));
        _store.Seed(assertion);
        return assertion;
    }

    private static GraphTraversalPlan Plan(string start, int depth = 2, int entities = 50, int assertions = 200, GraphTraversalDirection direction = GraphTraversalDirection.Both, IReadOnlySet<string>? predicates = null) =>
        new(Tenant, [EntityId("System", start)], predicates ?? DependsOn, depth, entities, assertions, direction);

    [Fact]
    public async Task TraversalStopsAtTheDepthLimit()
    {
        Edge("A", "B");
        Edge("B", "C");
        Edge("C", "D");

        var result = await Traversal().TraverseAsync(Plan("A", depth: 2, direction: GraphTraversalDirection.Outgoing), default);

        Assert.Equal([1, 2], result.Edges.Select(edge => edge.Depth).ToArray());
        Assert.DoesNotContain(result.Edges, edge => edge.ToEntityId == EntityId("System", "D"));
    }

    [Fact]
    public async Task EntityAndAssertionBudgetsTruncateTheTraversal()
    {
        foreach (var target in new[] { "B", "C", "D", "E" })
        {
            Edge("A", target);
        }

        var byEntities = await Traversal().TraverseAsync(Plan("A", entities: 3), default);
        var byAssertions = await Traversal().TraverseAsync(Plan("A", assertions: 2), default);

        Assert.True(byEntities.Truncated);
        Assert.Equal("entities", byEntities.TruncationReason);
        Assert.Equal(3, byEntities.VisitedEntityCount);
        Assert.True(byAssertions.Truncated);
        Assert.Equal(2, byAssertions.Edges.Count);
    }

    [Fact]
    public async Task IncomingTraversalFindsWhatDependsOnAnEntity()
    {
        Edge("A", "B");

        var incoming = await Traversal().TraverseAsync(Plan("B", direction: GraphTraversalDirection.Incoming), default);
        var outgoing = await Traversal().TraverseAsync(Plan("B", direction: GraphTraversalDirection.Outgoing), default);

        Assert.Equal(EntityId("System", "A"), Assert.Single(incoming.Edges).ToEntityId);
        Assert.Empty(outgoing.Edges);
    }

    [Fact]
    public async Task SymmetricRelationsAreReachedFromEitherEndWhateverTheDirection()
    {
        Edge("A", "B", "INTEGRATES_WITH");
        HashSet<string> integrates = ["INTEGRATES_WITH"];

        var fromA = await Traversal().TraverseAsync(Plan("A", direction: GraphTraversalDirection.Outgoing, predicates: integrates), default);
        var fromB = await Traversal().TraverseAsync(Plan("B", direction: GraphTraversalDirection.Outgoing, predicates: integrates), default);

        Assert.Single(fromA.Edges);
        Assert.Single(fromB.Edges);
    }

    [Fact]
    public async Task CyclesTerminateWithoutRepeatingAssertions()
    {
        Edge("A", "B");
        Edge("B", "A");

        var result = await Traversal().TraverseAsync(Plan("A", depth: 3), default);

        Assert.Equal(2, result.Edges.Count);
        Assert.Equal(result.Edges.Count, result.Edges.Select(edge => edge.Assertion.AssertionId).Distinct().Count());
    }

    [Theory]
    [InlineData(GraphProjectionStatus.Failed)]
    [InlineData(GraphProjectionStatus.Tombstoned)]
    [InlineData(GraphProjectionStatus.Pending)]
    public async Task DocumentsThatAreNotReadyAreNotServed(GraphProjectionStatus status)
    {
        Edge("A", "B");
        _store.SeedState(ReadyState(_record) with { Status = status });

        var result = await Traversal().TraverseAsync(Plan("A"), default);

        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task AssertionsFromAnInactiveRevisionAreNotServed()
    {
        var stale = File("item-1", "\"c:{1},0\"");
        Edge("A", "B", record: stale);

        var result = await Traversal().TraverseAsync(Plan("A"), default);

        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task RetractedAssertionsAreIgnored()
    {
        var edge = Edge("A", "B");
        _store.Seed(edge with { Status = GraphAssertionStatus.Retracted });

        Assert.Empty((await Traversal().TraverseAsync(Plan("A"), default)).Edges);
    }

    [Fact]
    public async Task AnotherTenantsAssertionsAreNeverReturned()
    {
        Edge("A", "B");
        var forged = Edge("A", "C") with { TenantId = "tenant-b" };
        _store.Seed(forged);
        var crossEvidence = Edge("A", "D");
        _store.Seed(crossEvidence with { Evidence = crossEvidence.Evidence with { TenantId = "tenant-b" } });

        var result = await Traversal().TraverseAsync(Plan("A"), default);
        var otherTenant = await Traversal().TraverseAsync(Plan("A") with { TenantId = "tenant-b" }, default);

        Assert.Equal(EntityId("System", "B"), Assert.Single(result.Edges).ToEntityId);
        Assert.Empty(otherTenant.Edges);
    }

    [Fact]
    public async Task OnlyThePlansPredicatesAreFollowed()
    {
        Edge("A", "B", "INTEGRATES_WITH");

        Assert.Empty((await Traversal().TraverseAsync(Plan("A"), default)).Edges);
    }

    [Fact]
    public async Task PlansOutsideTheOntologyOrLimitsAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Traversal().TraverseAsync(Plan("A", predicates: new HashSet<string> { "MENTIONS" }), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Traversal().TraverseAsync(Plan("A", depth: BoundedGraphTraversal.MaxDepthLimit + 1), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Traversal().TraverseAsync(Plan("A") with { StartEntityIds = ["bad/id"] }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Traversal().TraverseAsync(Plan("A") with { TenantId = "" }, default));
    }

    [Fact]
    public async Task ExceedingTheTimeBudgetIsATimeout()
    {
        Edge("A", "B");
        _store.ReadDelay = TimeSpan.FromSeconds(5);

        await Assert.ThrowsAsync<TimeoutException>(() => Traversal().TraverseAsync(Plan("A") with { Timeout = TimeSpan.FromMilliseconds(50) }, default));
    }

    [Fact]
    public async Task ExceedingTheRequestChargeBudgetStopsAfterTheCurrentLevel()
    {
        Edge("A", "B");
        Edge("B", "C");
        _store.ChargePerRead = 100;

        var result = await Traversal().TraverseAsync(Plan("A") with { MaxRequestCharge = 50 }, default);

        Assert.True(result.Truncated);
        Assert.Equal("requestCharge", result.TruncationReason);
        Assert.Equal(1, _store.AdjacencyReads);
    }

    [Fact]
    public async Task MergedEntitiesAreTraversedThroughTheirSurvivor()
    {
        var crm = Entity("System", "CRM");
        var portal = Entity("System", "Customer Portal");
        await _store.SaveEntityAsync(new GraphEntityRecord(crm, null, [portal.EntityId], [], null), default);
        await _store.SaveEntityAsync(new GraphEntityRecord(portal, crm.EntityId, [], [], null), default);
        Edge("Customer Portal", "ERP");

        var fromSurvivor = await Traversal().TraverseAsync(Plan("CRM"), default);
        var fromMerged = await Traversal().TraverseAsync(Plan("Customer Portal"), default);

        var edge = Assert.Single(fromSurvivor.Edges);
        Assert.Equal(crm.EntityId, edge.FromEntityId);
        Assert.Equal([crm.EntityId], fromMerged.StartEntityIds);
        Assert.Single(fromMerged.Edges);
    }
}
