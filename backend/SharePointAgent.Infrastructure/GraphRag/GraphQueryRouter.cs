using System.Text.RegularExpressions;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>Whether a question is worth a graph traversal, and which relations it is about.</summary>
public sealed record GraphRoute(bool IsGraphQuestion, IReadOnlySet<string> Predicates);

/// <summary>
/// Deterministic routing: fixed wording patterns map a question to the relations it asks about. It has no
/// model in the loop, so it adds no latency or cost and cannot be talked into a broader traversal; a
/// constrained model planner should replace it only if evaluation shows routing is the bottleneck.
/// Predicates the configured ontology does not define are dropped.
/// </summary>
public sealed partial class GraphQueryRouter(GraphOntology ontology)
{
    private static readonly (Regex Pattern, string[] Predicates)[] Rules =
    [
        (DependencyPattern(), ["DEPENDS_ON", "INTEGRATES_WITH"]),
        (IntegrationPattern(), ["INTEGRATES_WITH", "DEPENDS_ON"]),
        (OwnershipPattern(), ["OWNED_BY"]),
        (ImplementationPattern(), ["IMPLEMENTS", "APPLIES_TO"]),
        (SupersessionPattern(), ["SUPERSEDES"]),
        (ApplicabilityPattern(), ["APPLIES_TO", "IMPLEMENTS"]),
    ];

    public GraphRoute Route(string? question)
    {
        var predicates = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(question))
        {
            return new GraphRoute(false, predicates);
        }

        if (RelationshipPattern().IsMatch(question))
        {
            predicates.UnionWith(ontology.Relations.Keys);
        }

        foreach (var (pattern, candidates) in Rules)
        {
            if (pattern.IsMatch(question))
            {
                predicates.UnionWith(candidates.Where(ontology.Relations.ContainsKey));
            }
        }
        return new GraphRoute(predicates.Count > 0, predicates);
    }

    [GeneratedRegex(@"\b(depend\w*|impact\w*|affect\w*|upstream|downstream|rel(y|ies|ied|iant)|break(s|ing)?|outage\w*|blast radius)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DependencyPattern();

    [GeneratedRegex(@"\b(integrat\w*|interfac\w*|connect(s|ed|ion|ions)?\s+(to|with)|exchang\w*\s+data|feeds?\s+(into|from))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegrationPattern();

    [GeneratedRegex(@"\b(own(s|ed|er|ers|ership)?|responsible|accountable|who\s+(manages|maintains|runs))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnershipPattern();

    [GeneratedRegex(@"\b(implement\w*|complian\w*|compl(y|ies)|satisf(y|ies|ied)|fulfil\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImplementationPattern();

    [GeneratedRegex(@"\b(supersed\w*|replac(e|es|ed|ing)|obsolete|latest\s+version|newer\s+version|still\s+(valid|in\s+force))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupersessionPattern();

    [GeneratedRegex(@"\b(appl(y|ies)\s+to|applicable|govern\w*|subject\s+to|constrain\w*|regulat\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicabilityPattern();

    [GeneratedRegex(@"\b(related|relationship\w*|linked|connected|across\s+(documents|systems|projects))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelationshipPattern();
}
