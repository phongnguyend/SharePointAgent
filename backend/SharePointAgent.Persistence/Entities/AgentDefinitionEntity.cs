namespace SharePointAgent.Persistence;

public sealed class AgentDefinitionEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? ModelId { get; set; }
    public string Instructions { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
