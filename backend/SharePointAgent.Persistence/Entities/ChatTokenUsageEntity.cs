namespace SharePointAgent.Persistence;

/// <summary>
/// The chat usage ledger: one row per model request, naming the tools and skills that request asked for.
/// A turn is a tool-calling loop, so a turn is several rows sharing a <see cref="QuestionId"/>. Rows are
/// written as each response completes rather than when the turn ends, so a turn that is cancelled or
/// fails part way still records the requests it already paid for.
/// <para>
/// This is what monthly quotas and the Chat Usage report sum. Independent of messages and with no
/// cascading foreign keys: deleting or branching a conversation must never erase or duplicate billed
/// usage, and repeat accounting for the same question must not double-count.
/// </para>
/// </summary>
public sealed class ChatTokenUsageEntity
{
    /// <summary>
    /// <see cref="Sequence"/> for a row that stands for a whole turn rather than one request, written by
    /// the API when no per-request row reached the database — a sandbox that could not reach SQL, or a
    /// failed write. It carries the turn's reported total and names no tool.
    /// </summary>
    public const int TurnTotalSequence = -1;

    public Guid Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public int Day { get; set; }

    public int Month { get; set; }

    public Guid? UserId { get; set; }

    public Guid ConversationId { get; set; }

    public Guid QuestionId { get; set; }

    /// <summary>
    /// The position of this request in the turn's tool-calling loop, counted from zero, or
    /// <see cref="TurnTotalSequence"/> for a whole-turn fallback row.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// Null for usage recorded before the model was tracked, which the report offers as "unknown or
    /// historical".
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// The tools this response asked for, comma-separated, or null when it answered instead of calling
    /// one. Several names appear when the model asked for more than one tool in the same response.
    /// </summary>
    public string? ToolNames { get; set; }

    /// <summary>
    /// The skills named by this response's skill tool calls, comma-separated, or null when it called no
    /// skill tool. Skill tools are named after the operation rather than the skill — <c>load_skill</c>,
    /// <c>run_skill_script</c> — so the skill itself comes from the call's <c>skillName</c> argument.
    /// </summary>
    public string? SkillNames { get; set; }

    /// <summary>
    /// The scripts named by this response's <c>run_skill_script</c> calls, comma-separated, or null when
    /// it ran none. Each is a path relative to its skill, such as <c>scripts/resolve-dns.ps1</c>, which
    /// is what distinguishes a PowerShell skill script from a Python or Node.js one. The tool's argument
    /// for it is named <c>scriptName</c>.
    /// </summary>
    public string? ScriptNames { get; set; }

    /// <summary>
    /// Null rather than zero when the provider reported no usage for the request, so a missing figure is
    /// not summed as a free request.
    /// </summary>
    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? TotalTokens { get; set; }
}
