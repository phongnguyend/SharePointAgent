using Microsoft.Extensions.AI;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure.KnowledgeGraph;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class GraphQueryPlannerTests
{
    [Fact]
    public async Task ParsesEntityNamesAndOntologyRelationships()
    {
        var chat = new FakeChat("{\"entities\":[\"CRM\"],\"relationshipTypes\":[\"DEPENDS_ON\"]}");
        var result = await new AzureOpenAiGraphQueryPlanner(chat, Ontology).PlanAsync("CRM follows what?", default);

        Assert.Equal(["CRM"], result.Entities);
        Assert.Equal(["DEPENDS_ON"], result.RelationshipTypes);
        Assert.Contains("DEPENDS_ON", chat.Instructions);
    }

    [Theory]
    [InlineData("not json", false)]
    [InlineData("{\"entities\":[],\"relationshipTypes\":[]}", true)]
    public async Task RejectsMalformedOrTruncatedPlans(string json, bool truncated)
    {
        var planner = new AzureOpenAiGraphQueryPlanner(new FakeChat(json, truncated), Ontology);
        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync("question", default));
    }

    [Fact]
    public void UnsupportedPredicatesAreRejectedRatherThanBroadened()
    {
        Assert.Throws<ArgumentException>(() => new GraphQueryPlanValidator(Ontology).Validate(
            new GraphQueryPlan { Entities = ["CRM"], RelationshipTypes = ["INVENTED"] }));
    }

    [Fact]
    public void OmittedPredicatesExploreTheConfiguredOntology()
    {
        var route = new GraphQueryPlanValidator(Ontology).Validate(new GraphQueryPlan { Entities = ["CRM"] });
        Assert.True(route.SetEquals(Ontology.Relations.Keys));
    }

    private sealed class FakeChat(string json, bool truncated = false) : IChatClient
    {
        public string Instructions { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Instructions = messages.First().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
            {
                FinishReason = truncated ? ChatFinishReason.Length : ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
