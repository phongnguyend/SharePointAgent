using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

/// <summary>
/// The release-blocking security scenarios, end to end through routing, linking, traversal, verification,
/// and path pruning. The graph knows CRM → ERP (a public document) and ERP → Billing Engine (a restricted
/// one). User A can read only the public document; user B can read both.
/// </summary>
public sealed class GraphRetrievalSecurityTests
{
    private const string Secret = "Billing Engine";

    private const string PublicText = "The CRM depends on ERP for invoicing.";

    private const string RestrictedText = "ERP depends on the Billing Engine for settlement.";

    private readonly InMemoryGraphProjectionStore _store = new();
    private readonly InMemoryAliasIndex _aliases = new();
    private readonly InMemoryFileMetadata _metadata = new();
    private readonly FakeAuthorizedChunkStore _chunks = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly FileIndexRecord _public = File("public", name: "architecture.docx");
    private readonly FileIndexRecord _restricted = File("restricted", name: "finance.docx");
    private readonly GraphEntity _crm = Entity("System", "CRM");
    private readonly GraphEntity _erp = Entity("System", "ERP");
    private readonly GraphEntity _billing = Entity("System", Secret);

    public GraphRetrievalSecurityTests()
    {
        var writer = new GraphProjectionWriter(_store, Ontology, Options(), TimeProvider.System, NullLogger<GraphProjectionWriter>.Instance);
        Project(writer, _public, PublicText, _crm, _erp);
        Project(writer, _restricted, RestrictedText, _erp, _billing);
        _aliases.UpsertAsync([new EntityAliasEntry(Tenant, "System", "crm", _crm.EntityId), new EntityAliasEntry(Tenant, "System", "erp", _erp.EntityId)], default).Wait();
        _chunks.Grant("user-a", Drive, "public", 0);
        _chunks.Grant("user-b", Drive, "public", 0);
        _chunks.Grant("user-b", Drive, "restricted", 0);
    }

    private void Project(GraphProjectionWriter writer, FileIndexRecord record, string text, GraphEntity subject, GraphEntity @object)
    {
        _metadata.Put(record);
        _chunks.Put(record.DriveId, record.ItemId, 0, text, record.Name);
        writer.ApplySnapshotAsync(Snapshot(record, [subject, @object], [Assertion(subject, "DEPENDS_ON", @object, Evidence(record, 0, text))]), default).Wait();
    }

    private GraphRetrievalService Service(Action<GraphRagOptions>? configure = null, IGraphReader? reader = null) => new(
        Options(configure),
        SharePoint(),
        new GraphQueryRouter(Ontology),
        new GraphEntityLinker(_aliases),
        reader ?? new BoundedGraphTraversal(_store, Ontology, _logs.For<BoundedGraphTraversal>()),
        new GraphEvidenceVerifier(_chunks, _metadata, Options(configure)),
        _logs.For<GraphRetrievalService>());

    private Task<GraphRetrievalResult> AskAsync(string? user, string question = "What does the CRM depend on, directly or indirectly?", IReadOnlyList<SearchQueryHit>? baseline = null, GraphRetrievalService? service = null) =>
        (service ?? Service()).AugmentAsync(new GraphRetrievalRequest(user, question, baseline ?? []), default);

    [Fact]
    public async Task AUserWithAccessToEveryHopGetsTheMultiHopEvidence()
    {
        var result = await AskAsync("user-b");

        Assert.Equal(GraphRetrievalStatus.Augmented, result.Status);
        Assert.Equal([1, 2], result.Chunks.Select(chunk => chunk.Depth).ToArray());
        Assert.Contains(result.Chunks, chunk => chunk.Hit.Content == RestrictedText);
    }

    [Fact]
    public async Task AUserCannotInferARestrictedDocumentThroughTheGraph()
    {
        var result = await AskAsync("user-a");

        Assert.Equal(PublicText, Assert.Single(result.Chunks).Hit.Content);
        Assert.DoesNotContain(result.Chunks, chunk => chunk.Hit.ItemId == "restricted" || chunk.Hit.Content.Contains(Secret));
    }

    [Fact]
    public async Task EveryEdgeOfAPathNeedsAuthorizedEvidence()
    {
        // User C may read the restricted second hop but not the public first hop, so the second hop is
        // unreachable for them and must not be used.
        _chunks.Grant("user-c", Drive, "restricted", 0);

        var result = await AskAsync("user-c");

        Assert.Empty(result.Chunks);
        Assert.Equal(GraphRetrievalStatus.NoAuthorizedEvidence, result.Status);
    }

    [Fact]
    public async Task RevokedAccessTakesEffectOnTheNextQuestion()
    {
        Assert.Contains((await AskAsync("user-b")).Chunks, chunk => chunk.Hit.ItemId == "restricted");

        _chunks.Revoke("user-b", Drive, "restricted", 0);

        Assert.DoesNotContain((await AskAsync("user-b")).Chunks, chunk => chunk.Hit.ItemId == "restricted");
    }

    [Fact]
    public async Task ADeletedDocumentStopsAnsweringBeforeTheGraphIsCleanedUp()
    {
        _metadata.Remove(Drive, "restricted");

        var result = await AskAsync("user-b");

        Assert.DoesNotContain(result.Chunks, chunk => chunk.Hit.ItemId == "restricted");
    }

    [Fact]
    public async Task AnotherTenantsGraphIsUnreachable()
    {
        var result = await AskAsync("user-b", service: new GraphRetrievalService(
            Options(), SharePoint("tenant-b"), new GraphQueryRouter(Ontology), new GraphEntityLinker(_aliases),
            new BoundedGraphTraversal(_store, Ontology, NullLogger<BoundedGraphTraversal>.Instance),
            new GraphEvidenceVerifier(_chunks, _metadata, Options()), NullLogger<GraphRetrievalService>.Instance));

        Assert.Empty(result.Chunks);
        Assert.Equal(GraphRetrievalStatus.NoSeedEntities, result.Status);
    }

    [Fact]
    public async Task WithoutAUserTheGraphIsNeverRead()
    {
        var reader = Substitute.For<IGraphReader>();

        var result = await AskAsync(null, service: Service(reader: reader));

        Assert.Equal(GraphRetrievalStatus.NoUser, result.Status);
        Assert.Empty(reader.ReceivedCalls());
        Assert.Equal(0, _chunks.Calls);
    }

    [Fact]
    public async Task FailuresFallBackToTheBaselineWithoutLeakingDetails()
    {
        var reader = Substitute.For<IGraphReader>();
        reader.GetDocumentStatesAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<GraphDocumentState>>([]));
        reader.TraverseAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException($"store error near {Secret}"));

        var result = await AskAsync("user-b", service: Service(reader: reader));

        Assert.Equal(GraphRetrievalStatus.FellBack, result.Status);
        Assert.Empty(result.Chunks);
        Assert.DoesNotContain(_logs.Messages, message => message.Contains(Secret));
    }

    [Fact]
    public async Task ATimeoutFallsBackToTheBaseline()
    {
        _store.ReadDelay = TimeSpan.FromSeconds(5);

        var result = await AskAsync("user-b", service: Service(options => options.Traversal.TimeoutMilliseconds = 50));

        Assert.Equal(GraphRetrievalStatus.FellBack, result.Status);
    }

    [Fact]
    public async Task LogsNeverContainRestrictedTextOrNames()
    {
        await AskAsync("user-a");
        await AskAsync("user-b");
        await AskAsync("user-c");

        Assert.NotEmpty(_logs.Messages);
        Assert.DoesNotContain(_logs.Messages, message => message.Contains(Secret) || message.Contains("settlement"));
    }

    [Fact]
    public async Task ShadowModeHidesResultsFromTheAgentButNotFromEvaluation()
    {
        var shadow = Service(options =>
        {
            options.RetrievalEnabled = false;
            options.ShadowRetrieval = true;
        });

        var forAgent = await AskAsync("user-b", service: shadow);
        var forEvaluation = await shadow.AugmentAsync(new GraphRetrievalRequest("user-b", "What does the CRM depend on?", [], IncludeShadowResults: true), default);

        Assert.Equal(GraphRetrievalStatus.Shadow, forAgent.Status);
        Assert.Empty(forAgent.Chunks);
        Assert.NotEmpty(forEvaluation.Chunks);
    }

    [Fact]
    public async Task DisabledTenantsAndNonRelationshipQuestionsSkipTheGraph()
    {
        var disabled = await AskAsync("user-b", service: Service(options => options.EnabledTenantIds = ["someone-else"]));
        var plain = await AskAsync("user-b", "Summarize the CRM rollout timeline");

        Assert.Equal(GraphRetrievalStatus.Disabled, disabled.Status);
        Assert.Equal(GraphRetrievalStatus.NotGraphQuestion, plain.Status);
    }

    [Fact]
    public async Task BaselineChunksSeedTheTraversalAndAreNotRepeated()
    {
        var baseline = new[] { _chunks.Hit(Drive, "public", 0) };

        var result = await AskAsync("user-b", "What depends on what across documents here?", baseline);

        Assert.Equal(RestrictedText, Assert.Single(result.Chunks).Hit.Content);
    }

    [Fact]
    public async Task AddedChunksStayWithinTheBudget()
    {
        var result = await AskAsync("user-b", service: Service(options => options.Traversal.MaxAdditionalChunks = 1));

        Assert.Single(result.Chunks);
        Assert.Equal(1, result.Chunks[0].Depth);
    }

    [Fact]
    public async Task AnAmbiguousNameDoesNotStartATraversal()
    {
        await _aliases.UpsertAsync([new EntityAliasEntry(Tenant, "Project", "crm", EntityId("Project", "CRM"))], default);

        var result = await AskAsync("user-b", "What does CRM depend on?");

        Assert.Equal(GraphRetrievalStatus.NoSeedEntities, result.Status);
    }
}
