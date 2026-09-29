using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// What one model request cost and what it asked for. Reported as the response completes, not when the
/// turn does.
/// </summary>
/// <param name="Sequence">
/// The request's position in the turn's tool-calling loop, counted from zero.
/// </param>
/// <param name="ToolNames">The tools this response asked for; empty when it answered instead.</param>
/// <param name="SkillNames">The skills its skill tool calls named.</param>
/// <param name="ScriptNames">The scripts its <c>run_skill_script</c> calls named.</param>
/// <param name="Usage">
/// What the provider reported, or null when it reported nothing for this request.
/// </param>
public sealed record ChatRequestUsage(
    int Sequence,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> SkillNames,
    IReadOnlyList<string> ScriptNames,
    UsageDetails? Usage);

/// <summary>
/// Reports usage once per model request, with the tools and skills that request asked for.
/// <para>
/// It belongs below the agent's function-invocation layer, which is where <c>clientFactory</c> on
/// <c>AsAIAgent</c> puts it: the loop above calls this client once per round trip, so one call here is
/// one request and one billed response. Reporting from the streaming loop in
/// <see cref="ChatAgentService"/> instead would only ever see the turn's total.
/// </para>
/// <para>
/// A report is made whether the request succeeded, failed, or was cancelled, because the tokens are
/// spent either way — and because these rows are what monthly quotas sum, a failed turn is billed for
/// the requests it made. Reporting is best-effort: a failure to record is logged and swallowed, so it
/// can neither fail a turn nor hide the exception that was already on its way out.
/// </para>
/// </summary>
public sealed class TrackedChatClient(
    IChatClient inner,
    Func<ChatRequestUsage, CancellationToken, ValueTask> report,
    ILogger logger) : DelegatingChatClient(inner)
{
    /// <summary>
    /// The skill tools, whose names describe the operation rather than the skill. Which skill a call is
    /// for is in its <c>skillName</c> argument.
    /// </summary>
    private static readonly string[] SkillToolNames =
    [
        AgentSkillsProvider.LoadSkillToolName,
        AgentSkillsProvider.ReadSkillResourceToolName,
        AgentSkillsProvider.RunSkillScriptToolName,
    ];

    private int _sequence = -1;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var tally = new RequestTally();
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);

            // Only the calls from the contents here: a non-streaming response reports its usage on
            // ChatResponse.Usage, and a provider that also repeats it as UsageContent would otherwise
            // have this request billed twice.
            tally.AddCalls(response.Messages.SelectMany(message => message.Contents));
            tally.Add(response.Usage);
            return response;
        }
        finally
        {
            await ReportAsync(sequence, tally);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _sequence);

        // Read each update as it arrives rather than keeping the updates and reading them at the end:
        // once an update is yielded, the function-invocation loop above consumes it and may clear the
        // contents it has dealt with, so the tool calls would be gone by the time the stream finishes.
        var tally = new RequestTally();
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                tally.Add(update.Contents);
                yield return update;
            }
        }
        finally
        {
            await ReportAsync(sequence, tally);
        }
    }

    private async ValueTask ReportAsync(int sequence, RequestTally tally)
    {
        try
        {
            // Deliberately not the caller's token: the request is over and its tokens are spent, so a
            // cancelled turn must still leave the row behind.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await report(tally.ToUsage(sequence), timeout.Token);
        }
        catch (Exception ex)
        {
            // These rows are the billing ledger, so this loses that request's tokens. It is still
            // swallowed: the request is answered and paid for either way, and failing the turn here
            // would also mask whatever exception was already on its way out of the finally block.
            logger.LogError(ex, "Could not record billed usage for model request {Sequence} of this turn; its tokens are lost.", sequence);
        }
    }

    private static bool IsSkillTool(string name) => SkillToolNames.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// One argument of a tool call as a string. Arguments come from the model, so a name may be absent
    /// or hold something other than a string; either way this returns null rather than throwing.
    /// </summary>
    private static string? Argument(FunctionCallContent call, string name)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        var text = value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// What one request asked for and cost, copied out of the response as it arrives. Everything here is
    /// a string or a number taken immediately, never a reference to an <see cref="AIContent"/> the layer
    /// above is free to reuse or clear once this client has handed the update on.
    /// </summary>
    private sealed class RequestTally
    {
        private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
        private long? _input;
        private long? _output;
        private long? _total;
        private bool _reportedUsage;

        public void Add(IEnumerable<AIContent> contents)
        {
            foreach (var content in contents)
            {
                if (content is FunctionCallContent call)
                {
                    AddCall(call);
                }
                else if (content is UsageContent usage)
                {
                    Add(usage.Details);
                }
            }
        }

        /// <summary>The tool calls only, for a response whose usage is reported separately.</summary>
        public void AddCalls(IEnumerable<AIContent> contents)
        {
            foreach (var call in contents.OfType<FunctionCallContent>())
            {
                AddCall(call);
            }
        }

        private void AddCall(FunctionCallContent call)
        {
            if (string.IsNullOrWhiteSpace(call.Name))
            {
                return;
            }

            // A streamed call can arrive in more than one piece under the same ID; the later piece is
            // the more complete one. A call with no ID is kept on its own key.
            //
            // The argument names are the skill tools' own: "skillName" and, for run_skill_script,
            // "scriptName" — whose value is a path relative to the skill, such as
            // scripts/resolve-dns.ps1. SkillToolArgumentNamesAreUnchanged pins both.
            _calls[call.CallId ?? $"#{_calls.Count}"] =
                new Call(call.Name, Argument(call, "skillName"), Argument(call, "scriptName"));
        }

        public void Add(UsageDetails? details)
        {
            if (details is null)
            {
                return;
            }

            _reportedUsage = true;
            _input = (_input ?? 0) + (details.InputTokenCount ?? 0);
            _output = (_output ?? 0) + (details.OutputTokenCount ?? 0);
            _total = (_total ?? 0) + (details.TotalTokenCount ?? 0);
        }

        public ChatRequestUsage ToUsage(int sequence) => new(
            sequence,
            [.. _calls.Values.Select(x => x.Name)],
            [.. _calls.Values.Where(x => IsSkillTool(x.Name)).Select(x => x.SkillName).OfType<string>()],
            [.. _calls.Values.Select(x => x.ScriptPath).OfType<string>()],
            _reportedUsage
                ? new UsageDetails { InputTokenCount = _input, OutputTokenCount = _output, TotalTokenCount = _total }
                : null);

        private sealed record Call(string Name, string? SkillName, string? ScriptPath);
    }
}
