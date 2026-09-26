using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public static class SensitivityMetadata
{
    public static FileSensitivity Read(IEnumerable<KeyValuePair<string, string>> properties, string tenantId, FileSensitivity status)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties) metadata[property.Key] = property.Value;
        const string prefix = "MSIP_Label_";
        const string suffix = "_Enabled";
        var labels = metadata.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && p.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && (p.Value.Equals("true", StringComparison.OrdinalIgnoreCase) || p.Value == "1"))
            .Select(p => p.Key[prefix.Length..^suffix.Length])
            .Where(id => Guid.TryParse(id, out _))
            .ToArray();
        // Prefer our tenant. Do not arbitrarily assign one label if several foreign tenants are present.
        var own = labels.Where(id => metadata.GetValueOrDefault($"{prefix}{id}_SiteId")
            ?.Equals(tenantId, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var candidates = own.Length > 0 ? own : labels;
        if (candidates.Length != 1) return status with { IsLabeled = status.IsLabeled || labels.Length > 0 };
        var labelId = candidates[0];
        var name = metadata.GetValueOrDefault($"{prefix}{labelId}_Name");
        labelId = Guid.Parse(labelId).ToString();
        return status with { LabelId = labelId, LabelName = string.IsNullOrWhiteSpace(name) ? null : name, IsLabeled = true };
    }
}
