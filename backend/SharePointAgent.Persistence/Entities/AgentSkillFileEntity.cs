namespace SharePointAgent.Persistence;

public sealed class AgentSkillFileEntity
{
    public Guid Id { get; set; }

    public Guid AgentSkillDefinitionId { get; set; }

    public AgentSkillDefinitionEntity Definition { get; set; } = null!;

    public string Path { get; set; } = "";

    public int Version { get; set; } = 1;

    public long SizeBytes { get; set; }

    public string StorageKey { get; set; } = "";
}
