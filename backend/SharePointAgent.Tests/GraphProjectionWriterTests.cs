using Microsoft.Extensions.Logging.Abstractions;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class GraphProjectionWriterTests
{
    private readonly InMemoryGraphProjectionStore _store = new();

    private GraphProjectionWriter Writer() => new(_store, Ontology, Options(), TimeProvider.System, NullLogger<GraphProjectionWriter>.Instance);

    private static (FileIndexRecord Record, CanonicalGraphSnapshot Snapshot) CrmDependsOnErp(string itemId = "item-1", string cTag = "\"c:{1},1\"")
    {
        var record = File(itemId, cTag);
        var crm = Entity("System", "CRM");
        var erp = Entity("System", "ERP");
        return (record, Snapshot(record, [crm, erp], [Assertion(crm, "DEPENDS_ON", erp, Evidence(record, 0, "CRM depends on ERP."))]));
    }

    private async Task<GraphDocumentState> StateAsync(FileIndexRecord record) =>
        (await _store.GetDocumentStatesAsync(Tenant, [GraphDocumentIds.ForDriveItem(record.DriveId, record.ItemId)], default)).Single();

    [Fact]
    public async Task AppliedSnapshotsServeTheirRevision()
    {
        var (record, snapshot) = CrmDependsOnErp();

        await Writer().ApplySnapshotAsync(snapshot, default);

        var state = await StateAsync(record);
        Assert.True(state.IsServing(Revision(record)));
        Assert.Equal("x1", state.ActiveExtractionVersion);
        Assert.Single(state.Manifest);
        Assert.Empty(state.PendingManifest);
        Assert.Single(_store.Assertions, assertion => assertion.Status == GraphAssertionStatus.Asserted);
        Assert.Equal(2, (await _store.GetEntitiesAsync(Tenant, snapshot.Entities.Select(entity => entity.EntityId).ToList(), default)).Count);
    }

    [Fact]
    public async Task ReapplyingTheSameSnapshotWritesNothing()
    {
        var (_, snapshot) = CrmDependsOnErp();
        await Writer().ApplySnapshotAsync(snapshot, default);
        var operations = _store.Operations.Count;

        await Writer().ApplySnapshotAsync(snapshot, default);

        Assert.Equal(operations, _store.Operations.Count);
    }

    [Fact]
    public async Task ANewRevisionSwitchesThePointerThenRetractsOnlyTheOldRevisionsAssertions()
    {
        var (record, first) = CrmDependsOnErp();
        await Writer().ApplySnapshotAsync(first, default);
        var updated = File("item-1", "\"c:{1},2\"");
        var crm = Entity("System", "CRM");
        var billing = Entity("System", "Billing");
        var second = Snapshot(updated, [crm, billing], [Assertion(crm, "DEPENDS_ON", billing, Evidence(updated, 0, "CRM depends on Billing."))]);
        _store.Operations.Clear();

        await Writer().ApplySnapshotAsync(second, default);

        var state = await StateAsync(updated);
        Assert.True(state.IsServing(Revision(updated)));
        Assert.Equal(GraphAssertionStatus.Retracted, _store.Assertions.Single(assertion => assertion.AssertionId == first.Assertions[0].AssertionId).Status);
        Assert.Equal(GraphAssertionStatus.Asserted, _store.Assertions.Single(assertion => assertion.AssertionId == second.Assertions[0].AssertionId).Status);

        // The pointer moves before anything is retracted, so readers never see neither revision.
        Assert.True(_store.Operations.IndexOf("state:Ready") < _store.Operations.IndexOf("status:Retracted"));
        Assert.Empty(state.PendingManifest);
    }

    [Fact]
    public async Task DeletingADocumentTombstonesItBeforeRetractingAnything()
    {
        var (record, snapshot) = CrmDependsOnErp();
        await Writer().ApplySnapshotAsync(snapshot, default);
        _store.Operations.Clear();

        await Writer().RetractDocumentAsync(Tenant, GraphDocumentIds.ForDriveItem(record.DriveId, record.ItemId), default);

        Assert.Equal("state:Tombstoned", _store.Operations[0]);
        Assert.Equal("status:Retracted", _store.Operations[1]);
        var state = await StateAsync(record);
        Assert.Equal(GraphProjectionStatus.Tombstoned, state.Status);
        Assert.Null(state.ActiveRevision);
        Assert.Empty(state.PendingManifest);
        Assert.All(_store.Assertions, assertion => Assert.Equal(GraphAssertionStatus.Retracted, assertion.Status));
    }

    [Fact]
    public async Task DeletingOneDocumentKeepsSharedEntitiesAndOtherDocumentsAssertions()
    {
        var (first, firstSnapshot) = CrmDependsOnErp("item-1");
        var (_, secondSnapshot) = CrmDependsOnErp("item-2");
        await Writer().ApplySnapshotAsync(firstSnapshot, default);
        await Writer().ApplySnapshotAsync(secondSnapshot, default);

        await Writer().RetractDocumentAsync(Tenant, GraphDocumentIds.ForDriveItem(first.DriveId, first.ItemId), default);

        Assert.Equal(GraphAssertionStatus.Asserted, _store.Assertions.Single(assertion => assertion.AssertionId == secondSnapshot.Assertions[0].AssertionId).Status);
        Assert.Equal(2, (await _store.GetEntitiesAsync(Tenant, [EntityId("System", "CRM"), EntityId("System", "ERP")], default)).Count);
    }

    [Fact]
    public async Task AProjectionThatStopsHalfWayIsFinishedByTheNextAttempt()
    {
        var (record, snapshot) = CrmDependsOnErp();
        _store.FailOn = operation => operation == "upsertAssertions" ? new InvalidOperationException("store down") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().ApplySnapshotAsync(snapshot, default));

        var pending = await StateAsync(record);
        Assert.Equal(GraphProjectionStatus.Pending, pending.Status);
        Assert.Equal(snapshot.Assertions[0].AssertionId, Assert.Single(pending.PendingManifest).AssertionId);

        _store.FailOn = null;
        await Writer().ApplySnapshotAsync(snapshot, default);

        var ready = await StateAsync(record);
        Assert.True(ready.IsServing(Revision(record)));
        Assert.Empty(ready.PendingManifest);
        Assert.Equal(GraphAssertionStatus.Asserted, Assert.Single(_store.Assertions).Status);
    }

    [Fact]
    public async Task DeletingAfterAFailedProjectionRetractsWhatItHadWritten()
    {
        var (record, snapshot) = CrmDependsOnErp();
        _store.FailOn = operation => operation == "saveDocumentState" && _store.Operations.Contains("assertions") ? new InvalidOperationException("crash") : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().ApplySnapshotAsync(snapshot, default));
        _store.FailOn = null;

        await Writer().RetractDocumentAsync(Tenant, GraphDocumentIds.ForDriveItem(record.DriveId, record.ItemId), default);

        Assert.Equal(GraphAssertionStatus.Retracted, Assert.Single(_store.Assertions).Status);
    }

    [Fact]
    public async Task AConcurrentStateWriteSurfacesAsAConflictForRetry()
    {
        var (_, snapshot) = CrmDependsOnErp();
        _store.FailOn = operation => operation == "saveDocumentState" ? new GraphConcurrencyException("conflict") : null;

        await Assert.ThrowsAsync<GraphConcurrencyException>(() => Writer().ApplySnapshotAsync(snapshot, default));
    }

    [Fact]
    public async Task EntityConflictsAreRetriedOnTheFreshRecord()
    {
        var (_, snapshot) = CrmDependsOnErp();
        var failures = 1;
        _store.FailOn = operation => operation == "saveEntity" && Interlocked.Decrement(ref failures) >= 0 ? new GraphConcurrencyException("conflict") : null;

        await Writer().ApplySnapshotAsync(snapshot, default);

        Assert.Equal(2, (await _store.GetEntitiesAsync(Tenant, snapshot.Entities.Select(entity => entity.EntityId).ToList(), default)).Count);
    }

    [Fact]
    public async Task InvalidSnapshotsAreRejectedPermanently()
    {
        var (_, snapshot) = CrmDependsOnErp();

        var exception = await Assert.ThrowsAsync<GraphPermanentFailureException>(() => Writer().ApplySnapshotAsync(snapshot with { TenantId = "" }, default));

        Assert.Equal("snapshot.invalid", exception.Code);
        Assert.Empty(_store.Operations);
    }

    [Fact]
    public void MergingKeepsTheFirstCanonicalNameAndCollectsAliasesAndNewAttributes()
    {
        var stored = new GraphEntityRecord(
            new GraphEntity(Tenant, "system:1", "System", "CRM", [], new Dictionary<string, string> { ["vendor"] = "Contoso" }), null, [], [], "1");
        var incoming = new GraphEntity(Tenant, "system:1", "System", "Customer Relationship Management", ["crm"], new Dictionary<string, string> { ["vendor"] = "Other" });

        var merged = GraphProjectionWriter.Merge(stored, incoming)!;

        Assert.Equal("CRM", merged.Entity.CanonicalName);
        Assert.Equal(["Customer Relationship Management"], merged.Entity.Aliases);
        Assert.Equal("Contoso", merged.Entity.Attributes["vendor"]);
        Assert.Null(GraphProjectionWriter.Merge(merged, incoming));
    }

    [Fact]
    public async Task FailedRevisionsHideTheDocument()
    {
        var (record, snapshot) = CrmDependsOnErp();
        await Writer().ApplySnapshotAsync(snapshot, default);

        await Writer().MarkFailedAsync(Tenant, snapshot.DocumentId, "rev:new", "snapshot.invalid", default);

        var state = await StateAsync(record);
        Assert.Equal(GraphProjectionStatus.Failed, state.Status);
        Assert.False(state.IsServing(Revision(record)));
        Assert.Equal("snapshot.invalid", state.FailureCode);
    }

    [Fact]
    public async Task MergesRedirectTheSourceAndRecordWhoDidIt()
    {
        var (_, snapshot) = CrmDependsOnErp();
        var alias = Entity("System", "Customer Portal");
        await Writer().ApplySnapshotAsync(snapshot with { Entities = [.. snapshot.Entities, alias], Resolutions = [.. snapshot.Resolutions, new EntityResolutionDecision("1:e0", alias.EntityId, EntityResolutionMethod.NewEntity, [])] }, default);
        var crm = EntityId("System", "CRM");

        await Writer().MergeEntitiesAsync(Tenant, alias.EntityId, crm, "admin-oid", default);
        await Writer().MergeEntitiesAsync(Tenant, alias.EntityId, crm, "admin-oid", default);

        var records = await _store.GetEntitiesAsync(Tenant, [alias.EntityId, crm], default);
        var source = records.Single(record => record.Entity.EntityId == alias.EntityId);
        var target = records.Single(record => record.Entity.EntityId == crm);
        Assert.Equal(crm, source.RedirectToEntityId);
        Assert.Equal([alias.EntityId], target.MergedEntityIds);
        Assert.Equal("admin-oid", Assert.Single(source.History).Actor);
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().MergeEntitiesAsync(Tenant, crm, EntityId("Team", "Sales"), "admin-oid", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().MergeEntitiesAsync(Tenant, EntityId("System", "ERP"), alias.EntityId, "admin-oid", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Writer().MergeEntitiesAsync(Tenant, EntityId("System", "Unknown"), crm, "admin-oid", default));
    }
}
