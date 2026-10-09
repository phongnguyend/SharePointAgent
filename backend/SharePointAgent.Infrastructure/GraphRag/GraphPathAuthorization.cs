using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Keeps only the edges a reader can reach through authorized evidence alone. An edge counts only if its own
/// evidence is authorized and its starting entity is a start entity or is reached by an edge that already
/// counts. A second-hop fact behind an unauthorized first hop is dropped, because using it would reveal that
/// the hidden first hop exists.
/// </summary>
public static class GraphPathAuthorization
{
    public static IReadOnlyList<GraphTraversalEdge> SelectReachableEdges(
        IEnumerable<string> startEntityIds,
        IReadOnlyList<GraphTraversalEdge> edges,
        IReadOnlySet<string> authorizedAssertionIds)
    {
        var reachable = new HashSet<string>(startEntityIds, StringComparer.Ordinal);
        var candidates = edges.Where(edge => authorizedAssertionIds.Contains(edge.Assertion.AssertionId)).ToList();
        var selected = new List<GraphTraversalEdge>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);

        // Each pass extends the authorized frontier by one hop, so the loop ends after at most one pass per edge.
        bool changed;
        do
        {
            changed = false;
            foreach (var edge in candidates)
            {
                if (selectedIds.Contains(edge.Assertion.AssertionId) || !reachable.Contains(edge.FromEntityId))
                {
                    continue;
                }

                selected.Add(edge);
                selectedIds.Add(edge.Assertion.AssertionId);
                changed |= reachable.Add(edge.ToEntityId);
            }
        }
        while (changed);

        return selected
            .OrderBy(edge => edge.Depth)
            .ThenBy(edge => edge.Assertion.AssertionId, StringComparer.Ordinal)
            .ToList();
    }
}
