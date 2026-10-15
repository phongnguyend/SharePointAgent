using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class AgentSkillCacheOptions
{
    public const string SectionName = "AgentSkills:Cache";

    [Range(1, 3650)]
    public int MaxAgeDays { get; set; } = 7;
}
