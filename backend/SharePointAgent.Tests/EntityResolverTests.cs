using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class EntityResolverTests
{
    private readonly InMemoryAliasIndex _aliases = new();
    private readonly InMemoryGraphProjectionStore _store = new();

    private EntityResolver Resolver() => new(_aliases, _store, Ontology);

    private static EntityCandidate Candidate(string localRef, string type, string name, params (string Key, string Value)[] attributes) =>
        new(localRef, type, name, [], attributes.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public async Task AnAuthoritativeIdentifierDecidesWhateverTheName()
    {
        var result = await Resolver().ResolveAsync(Tenant, [
            Candidate("0:a", "Policy", "Data Retention Policy", ("policyNumber", "POL-7")),
            Candidate("1:a", "Policy", "Retention Standard", ("policyNumber", "pol-7"))
        ], default);

        Assert.All(result.Decisions, decision => Assert.Equal(EntityResolutionMethod.AuthoritativeIdentifier, decision.Method));
        Assert.Single(result.Decisions.Select(decision => decision.EntityId).Distinct());
    }

    [Fact]
    public async Task AnExactAliasOfOneExistingEntityResolvesToIt()
    {
        var existing = GraphIds.ForEntity(Tenant, "System", "id:cmdb:ci-42");
        await _aliases.UpsertAsync([new EntityAliasEntry(Tenant, "System", "customer portal", existing)], default);

        var decision = (await Resolver().ResolveAsync(Tenant, [Candidate("0:a", "System", "Customer  Portal")], default)).Decisions.Single();

        Assert.Equal(EntityResolutionMethod.ExactAlias, decision.Method);
        Assert.Equal(existing, decision.EntityId);
    }

    [Fact]
    public async Task AnAmbiguousNameIsNotMergedIntoEitherCandidate()
    {
        var first = GraphIds.ForEntity(Tenant, "Project", "id:projectcode:p-1");
        var second = GraphIds.ForEntity(Tenant, "Project", "id:projectcode:p-2");
        await _aliases.UpsertAsync([new EntityAliasEntry(Tenant, "Project", "phoenix", first), new EntityAliasEntry(Tenant, "Project", "phoenix", second)], default);

        var result = await Resolver().ResolveAsync(Tenant, [Candidate("0:a", "Project", "Phoenix")], default);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(EntityResolutionMethod.Ambiguous, decision.Method);
        Assert.Equal(EntityId("Project", "Phoenix"), decision.EntityId);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), decision.CandidateEntityIds);
        Assert.Empty(result.NewAliases);
    }

    [Fact]
    public async Task AnotherTenantsAliasesAreNeverUsed()
    {
        await _aliases.UpsertAsync([new EntityAliasEntry("tenant-b", "System", "crm", GraphIds.ForEntity("tenant-b", "System", "id:cmdb:1"))], default);

        var decision = (await Resolver().ResolveAsync(Tenant, [Candidate("0:a", "System", "CRM")], default)).Decisions.Single();

        Assert.Equal(EntityResolutionMethod.NewEntity, decision.Method);
        Assert.Equal(EntityId("System", "CRM"), decision.EntityId);
    }

    [Fact]
    public async Task ConcurrentDocumentsCreatingTheSameEntityAgreeOnItsId()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Resolver().ResolveAsync(Tenant, [Candidate("0:a", "System", "ERP")], default)));

        Assert.Single(results.Select(result => result.Decisions.Single().EntityId).Distinct());
    }

    [Fact]
    public async Task ANameMentionedWithAnIdentifierEarlierInTheDocumentResolvesToThatEntity()
    {
        var result = await Resolver().ResolveAsync(Tenant, [
            Candidate("0:a", "Policy", "Data Retention Policy", ("policyNumber", "POL-7")),
            Candidate("1:a", "Policy", "Data Retention Policy")
        ], default);

        Assert.Equal(result.Decisions[0].EntityId, result.Decisions[1].EntityId);
        Assert.Equal(EntityResolutionMethod.ExactAlias, result.Decisions[1].Method);
    }

    [Fact]
    public async Task MergedEntitiesResolveToTheirSurvivor()
    {
        var survivor = Entity("System", "CRM");
        var merged = Entity("System", "Customer Portal");
        await _store.SaveEntityAsync(new GraphEntityRecord(merged, survivor.EntityId, [], [], null), default);
        await _aliases.UpsertAsync([new EntityAliasEntry(Tenant, "System", "customer portal", merged.EntityId), new EntityAliasEntry(Tenant, "System", "crm portal", survivor.EntityId)], default);

        var byName = (await Resolver().ResolveAsync(Tenant, [Candidate("0:a", "System", "Customer Portal")], default)).Decisions.Single();
        var byBoth = (await Resolver().ResolveAsync(Tenant, [new EntityCandidate("0:b", "System", "Customer Portal", ["CRM Portal"], new Dictionary<string, string>())], default)).Decisions.Single();

        Assert.Equal(survivor.EntityId, byName.EntityId);
        Assert.Equal(EntityResolutionMethod.ExactAlias, byBoth.Method);
        Assert.Equal(survivor.EntityId, byBoth.EntityId);
    }

    [Fact]
    public async Task NewAliasesAreRecordedForLaterResolution()
    {
        var result = await Resolver().ResolveAsync(Tenant, [new EntityCandidate("0:a", "System", "CRM", ["Customer Relationship Management"], new Dictionary<string, string>())], default);

        Assert.Equal(["crm", "customer relationship management"], result.NewAliases.Select(alias => alias.NormalizedAlias).Order().ToArray());
        Assert.All(result.NewAliases, alias => Assert.Equal(Tenant, alias.TenantId));
    }
}
