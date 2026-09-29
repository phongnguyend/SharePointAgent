using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatTokenUsageTests
{
    /// <summary>
    /// The point of the whole design: a turn that calls a skill costs two model requests, and each one is
    /// reported on its own as it completes rather than summed and reported once at the end of the turn.
    /// This also pins the layering — the tracker only sees separate requests if the agent's
    /// function-invocation loop runs above it.
    /// </summary>
    [Fact]
    public async Task ReportsEachRequestSeparatelyWithItsToolAndSkillNames()
    {
        var reports = new List<ChatRequestUsage>();
        var inner = new ScriptedChatClient(
            [
                [
                    new FunctionCallContent("call-1", AgentSkillsProvider.RunSkillScriptToolName,
                        new Dictionary<string, object?>
                        {
                            ["skillName"] = "dns-lookup",
                            ["scriptName"] = "scripts/resolve-dns.ps1"
                        }),
                    new UsageContent(new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30, TotalTokenCount = 150 })
                ],
                [
                    new TextContent("example.com resolves to 93.184.216.34."),
                    new UsageContent(new UsageDetails { InputTokenCount = 260, OutputTokenCount = 18, TotalTokenCount = 278 })
                ],
            ]);
        var tracked = new TrackedChatClient(
            inner,
            (usage, _) =>
            {
                reports.Add(usage);
                return ValueTask.CompletedTask;
            },
            NullLogger.Instance);
        var agent = new ChatClientAgent(tracked, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(
                        (string skillName, string scriptName) => "[\"93.184.216.34\"]",
                        new AIFunctionFactoryOptions { Name = AgentSkillsProvider.RunSkillScriptToolName })
                ]
            }
        });

        var answer = "";
        await foreach (var update in agent.RunStreamingAsync("Resolve example.com."))
        {
            answer += update.Text;
        }

        Assert.Equal(2, inner.CallCount);
        Assert.Contains("93.184.216.34", answer);
        Assert.Equal(2, reports.Count);

        var first = reports[0];
        Assert.Equal(0, first.Sequence);
        Assert.Equal([AgentSkillsProvider.RunSkillScriptToolName], first.ToolNames);
        Assert.Equal(["dns-lookup"], first.SkillNames);
        Assert.Equal(["scripts/resolve-dns.ps1"], first.ScriptNames);
        Assert.Equal(120, first.Usage?.InputTokenCount);
        Assert.Equal(150, first.Usage?.TotalTokenCount);

        // The skill's own cost lands here, on the request that had to read the script's output back.
        var second = reports[1];
        Assert.Equal(1, second.Sequence);
        Assert.Empty(second.ToolNames);
        Assert.Empty(second.SkillNames);
        Assert.Equal(260, second.Usage?.InputTokenCount);
    }

    /// <summary>A request whose tokens were spent still leaves a report when the response fails.</summary>
    [Fact]
    public async Task ReportsRequestThatFailed()
    {
        var reports = new List<ChatRequestUsage>();
        var tracked = new TrackedChatClient(
            new ScriptedChatClient([], failWith: new TimeoutException("the deployment is busy")),
            (usage, _) =>
            {
                reports.Add(usage);
                return ValueTask.CompletedTask;
            },
            NullLogger.Instance);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in tracked.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello.")]))
            {
            }
        });

        var report = Assert.Single(reports);
        Assert.Equal(0, report.Sequence);
        Assert.Empty(report.ToolNames);
        Assert.Null(report.Usage);
    }

    /// <summary>
    /// The real agent is built with the skills provider supplying its tools and tool approval wrapped
    /// around it, neither of which the plain tool test above exercises. Skill calls have to reach the
    /// tracker through that arrangement too, or the ledger records the turn's requests without ever
    /// naming the skill that drove them.
    /// </summary>
    [Fact]
    public async Task RecordsSkillCallsThroughTheSkillsProviderAndToolApproval()
    {
        var reports = new List<ChatRequestUsage>();
        var inner = new ScriptedChatClient(
            [
                [
                    new FunctionCallContent("call-1", AgentSkillsProvider.LoadSkillToolName,
                        new Dictionary<string, object?> { ["skillName"] = "dns-lookup" }),
                    new UsageContent(new UsageDetails { InputTokenCount = 90, OutputTokenCount = 12, TotalTokenCount = 102 })
                ],
            ],
            fallback:
            [
                new TextContent("example.com resolves to 93.184.216.34."),
                new UsageContent(new UsageDetails { InputTokenCount = 410, OutputTokenCount = 20, TotalTokenCount = 430 })
            ]);
        var tracked = new TrackedChatClient(
            inner,
            (usage, _) =>
            {
                reports.Add(usage);
                return ValueTask.CompletedTask;
            },
            NullLogger.Instance);

        // The same shape ChatAgentService builds: skills as an AIContextProvider, approval around it.
        var agent = new ChatClientAgent(tracked, new ChatClientAgentOptions
        {
            Name = "SharePointSearchAgent",
            AIContextProviders = [ChatAgentSkills.CreateProvider()],
            ChatOptions = new ChatOptions { Instructions = "Answer questions." }
        })
            .AsBuilder()
            .UseToolApproval(new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule]
            })
            .Build();

        await foreach (var _ in agent.RunStreamingAsync("Resolve example.com."))
        {
        }

        Assert.Equal(2, reports.Count);
        Assert.Equal([AgentSkillsProvider.LoadSkillToolName], reports[0].ToolNames);
        Assert.Equal(["dns-lookup"], reports[0].SkillNames);
        Assert.Equal(102, reports[0].Usage?.TotalTokenCount);

        // The skill's instructions are paid for on the request that reads them back.
        Assert.Empty(reports[1].ToolNames);
        Assert.Equal(430, reports[1].Usage?.TotalTokenCount);
    }

    /// <summary>
    /// The API's end-of-turn safety net must stay quiet when the agent already recorded the turn,
    /// otherwise every turn would be billed twice — once per request and once in total.
    /// </summary>
    [Fact]
    public async Task TurnTotalFallbackIsSkippedWhenRequestRowsExist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var conversation = Guid.NewGuid();
        var question = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        // Billed usage keeps a restricting foreign key to its user, so the row has to have one.
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            DisplayName = "Quota",
            UserName = "quota@example.com",
            NormalizedUserName = "QUOTA@EXAMPLE.COM",
            Email = "quota@example.com",
            NormalizedEmail = "QUOTA@EXAMPLE.COM"
        };
        db.Users.Add(user);
        db.ChatTokenUsage.AddRange(
            NewRow(conversation, question, 0, startedAt, 120, 30),
            NewRow(conversation, question, 1, startedAt, 260, 18));
        await db.SaveChangesAsync();

        await MonthlyTokenQuota.RecordUsageAsync(
            db, user.Id, conversation, question, startedAt, new(380, 48, 428), "gpt-5-mini", default);

        Assert.Equal(2, await db.ChatTokenUsage.CountAsync());
        Assert.Equal(428, await db.ChatTokenUsage.SumAsync(x => x.TotalTokens ?? 0));

        // A different question is a different turn, so its fallback is written.
        var other = Guid.NewGuid();
        await MonthlyTokenQuota.RecordUsageAsync(
            db, user.Id, conversation, other, startedAt, new(10, 5, 15), "gpt-5-mini", default);
        var fallback = await db.ChatTokenUsage.SingleAsync(x => x.QuestionId == other);
        Assert.Equal(ChatTokenUsageEntity.TurnTotalSequence, fallback.Sequence);
        Assert.Equal(15, fallback.TotalTokens);
        Assert.Null(fallback.ToolNames);
    }

    private static ChatTokenUsageEntity NewRow(
        Guid conversationId, Guid questionId, int sequence, DateTimeOffset startedAt, long input, long output) => new()
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Day = MonthlyTokenQuota.DayKey(startedAt),
            Month = MonthlyTokenQuota.MonthKey(startedAt),
            ConversationId = conversationId,
            QuestionId = questionId,
            Sequence = sequence,
            ModelId = "gpt-5-mini",
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = input + output
        };

    [Fact]
    public async Task InsertUsesDatabaseDefaultAndStoresNames()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var databaseId = Guid.Parse("6f1c2b9a-1d3e-4a5b-9c8d-7e6f5a4b3c2d");
        connection.CreateFunction("NEWSEQUENTIALID", () => databaseId.ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var row = new ChatTokenUsageEntity
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Month = 202609,
            Day = 20260929,
            ConversationId = Guid.NewGuid(),
            QuestionId = Guid.NewGuid(),
            Sequence = 2,
            ModelId = "gpt-5-mini",
            ToolNames = "run_skill_script",
            SkillNames = "dns-lookup",
            ScriptNames = "scripts/resolve-dns.ps1",
            InputTokens = 120,
            OutputTokens = 30,
            TotalTokens = 150
        };

        Assert.Equal(Guid.Empty, row.Id);
        db.ChatTokenUsage.Add(row);
        await db.SaveChangesAsync();

        Assert.Equal(databaseId, row.Id);
        db.ChangeTracker.Clear();
        var stored = await db.ChatTokenUsage.SingleAsync();
        Assert.Equal(databaseId, stored.Id);
        Assert.Equal(2, stored.Sequence);
        Assert.Equal("run_skill_script", stored.ToolNames);
        Assert.Equal("dns-lookup", stored.SkillNames);
        Assert.Equal("scripts/resolve-dns.ps1", stored.ScriptNames);
        Assert.Equal(150, stored.TotalTokens);
        Assert.Null(stored.UserId);
    }

    /// <summary>
    /// Answers one scripted response per call, so a turn's requests can be told apart. Streaming splits
    /// each response into one update per content item, the way a provider does.
    /// </summary>
    private sealed class ScriptedChatClient(
        IReadOnlyList<IReadOnlyList<AIContent>> responses,
        Exception? failWith = null,
        IReadOnlyList<AIContent>? fallback = null) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. Next()])));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var responseId = $"response-{CallCount}";
            foreach (var content in Next())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                {
                    ResponseId = responseId,
                    MessageId = responseId
                };
            }

            await Task.CompletedTask;
        }

        private IReadOnlyList<AIContent> Next()
        {
            if (failWith is not null)
            {
                CallCount++;
                throw failWith;
            }

            // Past the script, keep answering, so a pipeline that needs more round trips than expected
            // finishes and the test can assert on what was recorded rather than on an index error.
            var index = CallCount++;
            return index < responses.Count ? responses[index]
                : fallback ?? throw new InvalidOperationException($"No scripted response for call {index}.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
