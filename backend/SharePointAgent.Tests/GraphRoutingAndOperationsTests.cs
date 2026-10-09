using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class GraphRoutingAndOperationsTests
{
    [Theory]
    [InlineData("What does the CRM depend on?", "DEPENDS_ON")]
    [InlineData("What would an ERP outage impact?", "DEPENDS_ON")]
    [InlineData("Which systems integrate with SAP?", "INTEGRATES_WITH")]
    [InlineData("Who owns the billing platform?", "OWNED_BY")]
    [InlineData("Which team is responsible for the portal?", "OWNED_BY")]
    [InlineData("Which policy supersedes the 2019 retention policy?", "SUPERSEDES")]
    [InlineData("Which requirements apply to the CRM?", "APPLIES_TO")]
    [InlineData("How is the portal related to the ERP?", "INTEGRATES_WITH")]
    public void RelationshipQuestionsAreRoutedToTheirPredicates(string question, string predicate)
    {
        var route = new GraphQueryRouter(Ontology).Route(question);

        Assert.True(route.IsGraphQuestion);
        Assert.Contains(predicate, route.Predicates);
    }

    [Theory]
    [InlineData("Summarize the onboarding guide")]
    [InlineData("What is the known downtime window?")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherQuestionsAreNotRouted(string? question)
    {
        Assert.False(new GraphQueryRouter(Ontology).Route(question).IsGraphQuestion);
    }

    [Fact]
    public void PredicatesTheOntologyDoesNotDefineAreNeverRouted()
    {
        var definition = GraphOntologyDefaults.CreateDefinition();
        definition.Version = "trimmed";
        definition.Relations.RemoveAll(relation => relation.Predicate != "OWNED_BY");

        Assert.False(new GraphQueryRouter(GraphOntology.Create(definition)).Route("What does the CRM depend on?").IsGraphQuestion);
    }

    [Fact]
    public void LinkingPhrasesSkipStopWordsAndPreferShortPhrasesWhenCapped()
    {
        var phrases = GraphEntityLinker.Phrases("What does the Data Retention Policy apply to?");
        var capped = GraphEntityLinker.Phrases(string.Join(' ', Enumerable.Range(0, 60).Select(index => $"word{index}")));

        Assert.Contains("data retention policy", phrases);
        Assert.Contains("data", phrases);
        Assert.DoesNotContain("what", phrases);
        Assert.DoesNotContain("the", phrases);
        Assert.Equal(120, capped.Count);
        Assert.Contains("word59", capped);
    }

    [Fact]
    public async Task LinkingUsesOnlyUnambiguousMatches()
    {
        var aliases = new InMemoryAliasIndex();
        await aliases.UpsertAsync([
            new EntityAliasEntry(Tenant, "System", "crm", "system:1"),
            new EntityAliasEntry(Tenant, "System", "phoenix", "system:2"),
            new EntityAliasEntry(Tenant, "Project", "phoenix", "project:3")
        ], default);

        var result = await new GraphEntityLinker(aliases).LinkAsync(Tenant, "Does Phoenix depend on the CRM?", default);

        Assert.Equal(["system:1"], result.EntityIds);
        Assert.Equal(1, result.AmbiguousPhrases);
    }

    [Fact]
    public void EvaluationReportsRecallPrecisionFullRecallAndLatencyPercentiles()
    {
        var cases = new[]
        {
            new GraphEvaluationCase("q1", ["a", "b"]),
            new GraphEvaluationCase("q2", ["c"])
        };
        var runs = new[]
        {
            new GraphEvaluationRun("q1", ["a", "x"], 100),
            new GraphEvaluationRun("q2", ["c"], 300)
        };

        var summary = GraphRagEvaluation.Summarize(cases, runs);

        Assert.Equal(2, summary.Cases);
        Assert.Equal(0.75, summary.MeanRecall, 3);
        Assert.Equal(0.75, summary.MeanPrecision, 3);
        Assert.Equal(0.5, summary.FullRecallRate, 3);
        Assert.Equal(100, summary.P50LatencyMilliseconds);
        Assert.Equal(300, summary.P95LatencyMilliseconds);
    }

    private static GraphIndexingMessageHandler Handler(IGraphIndexingPipeline pipeline, int maxAttempts = 3) => new(
        pipeline, Options(options => options.Extraction.MaxDeliveryAttempts = maxAttempts), TimeProvider.System, NullLogger<GraphIndexingMessageHandler>.Instance);

    private static FakeMessage Message(int attempt = 1, string? body = null) => new(
        BinaryData.FromString(body ?? JsonSerializer.Serialize(new GraphIndexingRequest(GraphIndexingRequestKind.Indexed, Drive, "item-1", DateTimeOffset.UnixEpoch), new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        attempt);

    [Fact]
    public async Task ProcessedMessagesAreCompleted()
    {
        var message = Message();

        await Handler(Substitute.For<IGraphIndexingPipeline>()).HandleAsync(message, default);

        Assert.Equal("complete", message.Outcome);
    }

    [Fact]
    public async Task PermanentFailuresAndMalformedMessagesAreDeadLetteredWithACode()
    {
        var pipeline = Substitute.For<IGraphIndexingPipeline>();
        pipeline.ProcessAsync(default!, default).ThrowsAsyncForAnyArgs(new GraphPermanentFailureException("snapshot.invalid", "invalid"));
        var permanent = Message();
        var malformed = Message(body: "{\"kind\":\"Nonsense\"}");

        await Handler(pipeline).HandleAsync(permanent, default);
        await Handler(pipeline).HandleAsync(malformed, default);

        Assert.Equal("deadletter:snapshot.invalid", permanent.Outcome);
        Assert.Equal("deadletter:InvalidMessage", malformed.Outcome);
    }

    [Fact]
    public async Task TransientFailuresAreRescheduledWithBackoffUntilTheBudgetIsSpent()
    {
        var pipeline = Substitute.For<IGraphIndexingPipeline>();
        pipeline.ProcessAsync(default!, default).ThrowsAsyncForAnyArgs(new GraphTransientFailureException("store.throttled", "busy"));
        var first = Message(attempt: 1);
        var last = Message(attempt: 3);

        await Handler(pipeline).HandleAsync(first, default);
        await Handler(pipeline).HandleAsync(last, default);

        Assert.Equal("reschedule:2", first.Outcome);
        Assert.True(first.EnqueueAt > DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("deadletter:RetriesExhausted", last.Outcome);
    }

    private sealed class FakeMessage(BinaryData body, int attempt) : IGraphMessageContext
    {
        public string? Outcome { get; private set; }

        public DateTimeOffset EnqueueAt { get; private set; }

        public BinaryData Body { get; } = body;

        public int Attempt { get; } = attempt;

        public CancellationToken CancellationToken => CancellationToken.None;

        public Task CompleteAsync()
        {
            Outcome = "complete";
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string reason, string description)
        {
            Outcome = $"deadletter:{reason}";
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(int nextAttempt, DateTimeOffset enqueueAt)
        {
            Outcome = $"reschedule:{nextAttempt}";
            EnqueueAt = enqueueAt;
            return Task.CompletedTask;
        }
    }
}
