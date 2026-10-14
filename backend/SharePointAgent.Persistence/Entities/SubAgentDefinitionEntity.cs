namespace SharePointAgent.Persistence;

/// <summary>A private tool owned by one top-level agent, never a conversation agent.</summary>
public sealed class SubAgentDefinitionEntity
{
    public Guid Id { get; set; }

    public Guid ParentAgentId { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string ModelId { get; set; } = "";

    public string Instructions { get; set; } = "";

    public string[] Tools { get; set; } = [];

    public string[] Skills { get; set; } = [];

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
