namespace SharePointAgent.Persistence;

public sealed class AgentSkillVersionEntity
{
    public Guid Id { get; set; }

    public Guid AgentSkillDefinitionId { get; set; }

    public AgentSkillDefinitionEntity Definition { get; set; } = null!;

    public int Version { get; set; }

    public string Description { get; set; } = "";

    public string ContentHash { get; set; } = "";

    public DateTimeOffset CreatedAtUtc { get; set; }
}
