using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.KnowledgeGraph;

/// <summary>Validates an explicit query plan against the configured ontology.</summary>
public sealed class GraphQueryPlanValidator(GraphOntology ontology)
{
    public IReadOnlySet<string> Validate(GraphQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Entities is null || plan.RelationshipTypes is null || plan.Entities.Count > 20 || plan.RelationshipTypes.Count > 30
            || plan.Entities.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 200))
        {
            throw new ArgumentException("Provide at most 20 entity names of 1-200 characters and 30 relationship types.");
        }

        var predicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in plan.RelationshipTypes)
        {
            var predicate = value?.Trim().ToUpperInvariant() ?? "";
            if (!ontology.Relations.ContainsKey(predicate))
            {
                throw new ArgumentException("The query contains an unsupported relationship type.");
            }
            predicates.Add(predicate);
        }
        if (predicates.Count == 0)
        {
            predicates.UnionWith(ontology.Relations.Keys);
        }
        return predicates;
    }

}
