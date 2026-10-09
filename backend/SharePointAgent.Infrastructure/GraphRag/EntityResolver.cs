using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Resolves extracted mentions to canonical entities, in this order of preference:
/// <list type="number">
/// <item>An authoritative identifier (the type's identifier attribute, such as a policy number) always
/// decides, because two mentions with the same identifier are the same thing whatever they are called.</item>
/// <item>An exact normalized name or alias that names exactly one entity of the same type, in this tenant.</item>
/// <item>Otherwise a name-keyed entity. When the name matches several entities nothing is merged; the
/// mention gets its own entity and the candidates are recorded for an operator.</item>
/// </list>
/// Every ID is derived deterministically from the tenant, type, and key, so two documents creating the same
/// entity at the same time produce the same ID and their writes converge instead of duplicating. Entity
/// similarity alone never merges anything.
/// </summary>
public sealed class EntityResolver(IEntityAliasIndex aliases, IGraphProjectionStore store, GraphOntology ontology) : IEntityResolver
{
    private const int MaxRedirectHops = 5;

    private const int MaxCandidates = 10;

    public async Task<EntityResolutionResult> ResolveAsync(string tenantId, IReadOnlyList<EntityCandidate> candidates, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        foreach (var candidate in candidates)
        {
            if (!ontology.IsEntityType(candidate.EntityType))
            {
                throw new ArgumentException("Every candidate must have an entity type of the ontology.", nameof(candidates));
            }
        }

        var decisions = new Dictionary<string, EntityResolutionDecision>(StringComparer.Ordinal);

        // Identifier mentions are resolved first and their names added to an in-batch alias map, so a later
        // mention of the same name in this document finds the identified entity before the index knows it.
        var batchAliases = new Dictionary<(string Type, string Alias), HashSet<string>>();
        foreach (var candidate in candidates)
        {
            if (IdentifierKey(candidate) is { } key)
            {
                var entityId = GraphIds.ForEntity(tenantId, candidate.EntityType, key);
                decisions[candidate.LocalRef] = new EntityResolutionDecision(candidate.LocalRef, entityId, EntityResolutionMethod.AuthoritativeIdentifier, []);
                foreach (var name in Names(candidate))
                {
                    AddBatchAlias(batchAliases, candidate.EntityType, name, entityId);
                }
            }
        }

        var lookups = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var group in candidates.Where(candidate => !decisions.ContainsKey(candidate.LocalRef)).GroupBy(candidate => candidate.EntityType))
        {
            var names = group.SelectMany(Names).Distinct(StringComparer.Ordinal).ToList();
            lookups[group.Key] = names.Count == 0
                ? new Dictionary<string, IReadOnlyList<string>>()
                : await aliases.FindAsync(tenantId, group.Key, names, cancellationToken);
        }

        var redirects = new RedirectResolver(store, tenantId);
        foreach (var candidate in candidates)
        {
            if (decisions.TryGetValue(candidate.LocalRef, out var identified))
            {
                decisions[candidate.LocalRef] = identified with { EntityId = await redirects.CanonicalAsync(identified.EntityId, cancellationToken) };
                continue;
            }

            var matched = new HashSet<string>(StringComparer.Ordinal);
            var lookup = lookups.GetValueOrDefault(candidate.EntityType);
            foreach (var name in Names(candidate))
            {
                if (lookup is not null && lookup.TryGetValue(name, out var indexed))
                {
                    matched.UnionWith(indexed);
                }

                if (batchAliases.TryGetValue((candidate.EntityType, name), out var inBatch))
                {
                    matched.UnionWith(inBatch);
                }
            }

            // Two aliases of one merged entity are one match, not an ambiguity.
            var canonical = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in matched.Order(StringComparer.Ordinal))
            {
                canonical.Add(await redirects.CanonicalAsync(id, cancellationToken));
            }

            EntityResolutionDecision decision;
            if (canonical.Count == 1)
            {
                decision = new EntityResolutionDecision(candidate.LocalRef, canonical.Single(), EntityResolutionMethod.ExactAlias, []);
            }
            else
            {
                var nameKeyed = await redirects.CanonicalAsync(
                    GraphIds.ForEntity(tenantId, candidate.EntityType, GraphEntityKey.FromName(candidate.Name)), cancellationToken);
                decision = canonical.Count == 0
                    ? new EntityResolutionDecision(candidate.LocalRef, nameKeyed, EntityResolutionMethod.NewEntity, [])
                    : new EntityResolutionDecision(candidate.LocalRef, nameKeyed, EntityResolutionMethod.Ambiguous,
                        canonical.Where(id => id != nameKeyed).Order(StringComparer.Ordinal).Take(MaxCandidates).ToList());
            }
            decisions[candidate.LocalRef] = decision;
        }

        var ordered = candidates.Select(candidate => decisions[candidate.LocalRef]).ToList();
        foreach (var decision in ordered)
        {
            GraphRagMetrics.Resolved(decision.Method.ToString());
        }

        // An ambiguous name is not added as an alias of the entity it fell back to; doing so would only make
        // the ambiguity permanent.
        var newAliases = candidates
            .Zip(ordered)
            .Where(pair => pair.Second.Method != EntityResolutionMethod.Ambiguous)
            .SelectMany(pair => Names(pair.First).Select(name => new EntityAliasEntry(tenantId, pair.First.EntityType, name, pair.Second.EntityId)))
            .Distinct()
            .ToList();
        return new EntityResolutionResult(ordered, newAliases);
    }

    private string? IdentifierKey(EntityCandidate candidate)
    {
        if (ontology.EntityTypes[candidate.EntityType].IdentifierAttribute is not { } attribute
            || !candidate.Attributes.TryGetValue(attribute, out var value))
        {
            return null;
        }

        // Identifiers such as "POL-001" and "pol-001" are folded together like names are.
        var normalized = GraphNormalization.NormalizeName(value);
        return normalized.Length == 0 ? null : GraphEntityKey.FromIdentifier(attribute.ToLowerInvariant(), normalized);
    }

    private static IEnumerable<string> Names(EntityCandidate candidate) =>
        candidate.Aliases.Prepend(candidate.Name)
            .Select(GraphNormalization.NormalizeName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static void AddBatchAlias(Dictionary<(string Type, string Alias), HashSet<string>> map, string type, string alias, string entityId)
    {
        if (!map.TryGetValue((type, alias), out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            map[(type, alias)] = ids;
        }
        ids.Add(entityId);
    }

    /// <summary>Follows merge redirects to the surviving entity, reading each entity at most once.</summary>
    private sealed class RedirectResolver(IGraphProjectionStore store, string tenantId)
    {
        private readonly Dictionary<string, string?> _redirects = new(StringComparer.Ordinal);

        public async Task<string> CanonicalAsync(string entityId, CancellationToken cancellationToken)
        {
            var current = entityId;
            for (var hop = 0; hop < MaxRedirectHops; hop++)
            {
                if (!_redirects.TryGetValue(current, out var redirect))
                {
                    var record = (await store.GetEntitiesAsync(tenantId, [current], cancellationToken)).FirstOrDefault();
                    redirect = record?.Entity.TenantId == tenantId ? record.RedirectToEntityId : null;
                    _redirects[current] = redirect;
                }

                if (redirect is null)
                {
                    return current;
                }
                current = redirect;
            }
            return current;
        }
    }
}
