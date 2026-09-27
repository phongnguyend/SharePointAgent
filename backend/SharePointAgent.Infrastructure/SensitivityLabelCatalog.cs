using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace SharePointAgent.Infrastructure;

/// <summary>Reads the current tenant label catalog on every request, without a local name cache.</summary>
public sealed class SensitivityLabelCatalog(GraphServiceClient graph)
{
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var request = graph.Security.DataSecurityAndGovernance.SensitivityLabels;
        var page = await request.GetAsync(cancellationToken: cancellationToken);
        while (page is not null)
        {
            foreach (var label in page.Value ?? [])
            {
                Add(label, null, names);
            }

            if (string.IsNullOrEmpty(page.OdataNextLink))
            {
                break;
            }

            page = await request.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
        return names;
    }

    private static void Add(SensitivityLabel label, string? parent, Dictionary<string, string> names)
    {
        var name = string.IsNullOrWhiteSpace(label.DisplayName) ? label.Name : label.DisplayName;
        var fullName = string.IsNullOrWhiteSpace(name) ? parent : parent is null ? name : $"{parent} · {name}";
        if (!string.IsNullOrEmpty(label.Id) && !string.IsNullOrWhiteSpace(fullName))
        {
            names[label.Id.ToLowerInvariant()] = fullName;
        }

        foreach (var child in label.Sublabels ?? [])
        {
            Add(child, fullName, names);
        }
    }
}
