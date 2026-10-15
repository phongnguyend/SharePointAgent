namespace SharePointAgent.Persistence;

public sealed class AgentSkillDefinitionEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string Scope { get; set; } = "global";

    public bool IsDefault { get; set; }

    public string ContentHash { get; set; } = "";

    public int ActiveVersion { get; set; } = 1;

    public int LastVersion { get; set; } = 1;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<AgentSkillFileEntity> Files { get; set; } = [];
}
