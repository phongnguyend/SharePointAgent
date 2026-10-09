using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SharePointAgent.Domain;

/// <summary>
/// The configurable form of an ontology, shaped for configuration binding. It is compiled into a
/// <see cref="GraphOntology"/>, which rejects a definition that is inconsistent, before anything uses it.
/// </summary>
public sealed class GraphOntologyDefinition
{
    public string Version { get; set; } = "";

    public List<GraphEntityTypeDefinition> EntityTypes { get; set; } = [];

    public List<GraphRelationDefinition> Relations { get; set; } = [];
}

public sealed class GraphEntityTypeDefinition
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>
    /// The attribute keys an entity of this type may carry. Anything else is rejected, which keeps extracted
    /// document text from being copied into the graph store as free-form attributes.
    /// </summary>
    public List<string> Attributes { get; set; } = [];

    /// <summary>
    /// The attribute, if any, whose value identifies an entity of this type authoritatively, such as a policy
    /// number. Two mentions with the same identifier are the same entity whatever they are called; two
    /// mentions that only share a name are not merged on that basis alone.
    /// </summary>
    public string? IdentifierAttribute { get; set; }
}

/// <summary>
/// One relation and the subject and object types it may connect. Relations are directed from subject to
/// object unless <see cref="Symmetric"/> is set; a symmetric relation is stored once per unordered pair.
/// </summary>
public sealed class GraphRelationDefinition
{
    public string Predicate { get; set; } = "";

    public string Description { get; set; } = "";

    public List<string> SubjectTypes { get; set; } = [];

    public List<string> ObjectTypes { get; set; } = [];

    public bool Symmetric { get; set; }

    /// <summary>Requires the subject and object to be of the same type, as in a policy superseding a policy.</summary>
    public bool RequireSameEntityType { get; set; }

    public bool AllowSelfReference { get; set; }

    /// <summary>
    /// Marks a relation that cannot hold in both directions, such as one policy superseding another. When one
    /// document asserts it both ways, both assertions are recorded as disputed rather than either being trusted.
    /// </summary>
    public bool Antisymmetric { get; set; }
}

public sealed record GraphEntityTypeRule(string Name, string Description, IReadOnlySet<string> Attributes, string? IdentifierAttribute = null);

public sealed record GraphRelationRule(
    string Predicate,
    string Description,
    IReadOnlySet<string> SubjectTypes,
    IReadOnlySet<string> ObjectTypes,
    bool Symmetric,
    bool RequireSameEntityType,
    bool AllowSelfReference,
    bool Antisymmetric = false);

public sealed class GraphOntologyDefinitionException(IReadOnlyList<GraphValidationError> errors)
    : Exception($"The graph ontology definition is invalid: {string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"))}")
{
    public IReadOnlyList<GraphValidationError> Errors { get; } = errors;
}

/// <summary>
/// A validated, immutable ontology: the entity types, the predicates, and the subject, predicate, and object
/// triples that are allowed. Names are matched exactly; normalizing extracted text into these names is the
/// extractor's job, so the validator never guesses what an unknown type or predicate was meant to be.
/// </summary>
public sealed partial class GraphOntology
{
    private GraphOntology(
        string version,
        IReadOnlyDictionary<string, GraphEntityTypeRule> entityTypes,
        IReadOnlyDictionary<string, GraphRelationRule> relations)
    {
        Version = version;
        EntityTypes = entityTypes;
        Relations = relations;
    }

    public string Version { get; }

    public IReadOnlyDictionary<string, GraphEntityTypeRule> EntityTypes { get; }

    public IReadOnlyDictionary<string, GraphRelationRule> Relations { get; }

    public static GraphOntology Create(GraphOntologyDefinition definition)
    {
        var errors = ValidateDefinition(definition);
        if (errors.Count > 0)
        {
            throw new GraphOntologyDefinitionException(errors);
        }

        var entityTypes = definition.EntityTypes.ToDictionary(
            type => type.Name,
            type => new GraphEntityTypeRule(
                type.Name, type.Description, type.Attributes.ToHashSet(StringComparer.Ordinal),
                string.IsNullOrEmpty(type.IdentifierAttribute) ? null : type.IdentifierAttribute),
            StringComparer.Ordinal);
        var relations = definition.Relations.ToDictionary(
            relation => relation.Predicate,
            relation => new GraphRelationRule(
                relation.Predicate,
                relation.Description,
                relation.SubjectTypes.ToHashSet(StringComparer.Ordinal),
                relation.ObjectTypes.ToHashSet(StringComparer.Ordinal),
                relation.Symmetric,
                relation.RequireSameEntityType,
                relation.AllowSelfReference,
                relation.Antisymmetric),
            StringComparer.Ordinal);
        return new GraphOntology(definition.Version, entityTypes, relations);
    }

    public bool IsEntityType([NotNullWhen(true)] string? entityType) => entityType is not null && EntityTypes.ContainsKey(entityType);

    public bool TryGetRelation([NotNullWhen(true)] string? predicate, [MaybeNullWhen(false)] out GraphRelationRule relation)
    {
        if (predicate is not null && Relations.TryGetValue(predicate, out var found))
        {
            relation = found;
            return true;
        }

        relation = null;
        return false;
    }

    public bool IsAllowedTriple(string subjectType, string predicate, string objectType)
    {
        if (!TryGetRelation(predicate, out var relation))
        {
            return false;
        }

        if (relation.RequireSameEntityType && !string.Equals(subjectType, objectType, StringComparison.Ordinal))
        {
            return false;
        }
        return relation.SubjectTypes.Contains(subjectType) && relation.ObjectTypes.Contains(objectType);
    }

    /// <summary>
    /// Checks a definition without throwing, so configuration validation can report every problem at
    /// start-up. Messages may name types and predicates because they come from configuration, not documents.
    /// </summary>
    public static IReadOnlyList<GraphValidationError> ValidateDefinition(GraphOntologyDefinition? definition)
    {
        var errors = new List<GraphValidationError>();
        if (definition is null)
        {
            errors.Add(new(GraphValidationCodes.OntologyInvalid, "", "The ontology definition is missing."));
            return errors;
        }

        if (string.IsNullOrEmpty(definition.Version) || !VersionPattern().IsMatch(definition.Version))
        {
            errors.Add(new(GraphValidationCodes.OntologyInvalid, "version", "The version must be 1-64 letters, digits, '.', '_', or '-'."));
        }

        var entityTypes = definition.EntityTypes ?? [];
        if (entityTypes.Count == 0)
        {
            errors.Add(new(GraphValidationCodes.OntologyInvalid, "entityTypes", "At least one entity type is required."));
        }

        // Entity IDs are prefixed with the lowercased type, so types differing only in case would collide.
        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definedTypes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entityTypes.Count; index++)
        {
            var path = $"entityTypes[{index}]";
            var type = entityTypes[index];
            if (type is null || string.IsNullOrEmpty(type.Name) || !EntityTypePattern().IsMatch(type.Name))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.name", "An entity type name must be PascalCase letters and digits, at most 64 characters."));
                continue;
            }

            if (!typeNames.Add(type.Name))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.name", $"The entity type '{type.Name}' is defined more than once."));
                continue;
            }
            definedTypes.Add(type.Name);

            var attributes = type.Attributes ?? [];
            var attributeNames = new HashSet<string>(StringComparer.Ordinal);
            for (var attributeIndex = 0; attributeIndex < attributes.Count; attributeIndex++)
            {
                var attribute = attributes[attributeIndex];
                if (string.IsNullOrEmpty(attribute) || !AttributePattern().IsMatch(attribute))
                {
                    errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.attributes[{attributeIndex}]", "An attribute key must be camelCase letters and digits, at most 64 characters."));
                }
                else if (!attributeNames.Add(attribute))
                {
                    errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.attributes[{attributeIndex}]", $"The attribute '{attribute}' is listed more than once."));
                }
            }

            if (!string.IsNullOrEmpty(type.IdentifierAttribute) && !attributeNames.Contains(type.IdentifierAttribute))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.identifierAttribute", $"The identifier attribute '{type.IdentifierAttribute}' must be one of the type's attributes."));
            }
        }

        var relations = definition.Relations ?? [];
        var predicates = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < relations.Count; index++)
        {
            var path = $"relations[{index}]";
            var relation = relations[index];
            if (relation is null || string.IsNullOrEmpty(relation.Predicate) || relation.Predicate.Length > 64 || !PredicatePattern().IsMatch(relation.Predicate))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.predicate", "A predicate must be UPPER_SNAKE_CASE, at most 64 characters."));
                continue;
            }

            if (!predicates.Add(relation.Predicate))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}.predicate", $"The predicate '{relation.Predicate}' is defined more than once."));
                continue;
            }

            var subjectTypes = relation.SubjectTypes ?? [];
            var objectTypes = relation.ObjectTypes ?? [];
            ValidateRelationTypes(errors, $"{path}.subjectTypes", subjectTypes, definedTypes);
            ValidateRelationTypes(errors, $"{path}.objectTypes", objectTypes, definedTypes);

            if (relation.Symmetric && !subjectTypes.ToHashSet(StringComparer.Ordinal).SetEquals(objectTypes))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, path, $"The symmetric relation '{relation.Predicate}' must allow the same subject and object types."));
            }

            if (relation.Symmetric && relation.Antisymmetric)
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, path, $"The relation '{relation.Predicate}' cannot be both symmetric and antisymmetric."));
            }

            if (relation.RequireSameEntityType && !subjectTypes.Intersect(objectTypes, StringComparer.Ordinal).Any())
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, path, $"The relation '{relation.Predicate}' requires one entity type on both ends but its subject and object types do not overlap."));
            }
        }
        return errors;
    }

    private static void ValidateRelationTypes(
        List<GraphValidationError> errors, string path, List<string> types, HashSet<string> definedTypes)
    {
        if (types.Count == 0)
        {
            errors.Add(new(GraphValidationCodes.OntologyInvalid, path, "At least one entity type is required."));
            return;
        }

        for (var index = 0; index < types.Count; index++)
        {
            if (types[index] is null || !definedTypes.Contains(types[index]))
            {
                errors.Add(new(GraphValidationCodes.OntologyInvalid, $"{path}[{index}]", $"The entity type '{types[index]}' is not defined."));
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,63}$")]
    private static partial Regex EntityTypePattern();

    [GeneratedRegex("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$")]
    private static partial Regex PredicatePattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$")]
    private static partial Regex AttributePattern();
}

/// <summary>
/// The starting ontology: a small set of candidate types and relations to be trimmed or extended for the
/// actual corpus through configuration. Co-occurrence in a chunk is never a relation; each predicate is a
/// specific, directed claim the text must state.
/// </summary>
public static class GraphOntologyDefaults
{
    public const string Version = "v1";

    public static GraphOntologyDefinition CreateDefinition() => new()
    {
        Version = Version,
        EntityTypes =
        [
            new() { Name = "System", Description = "A software system, application, or service.", Attributes = ["vendor"] },
            new() { Name = "Project", Description = "A time-bound initiative or programme of work.", Attributes = ["projectCode"], IdentifierAttribute = "projectCode" },
            new() { Name = "Team", Description = "An organizational unit or group of people." },
            new() { Name = "Person", Description = "A named individual acting in a role." },
            new() { Name = "Policy", Description = "A governing policy, standard, or guideline.", Attributes = ["policyNumber"], IdentifierAttribute = "policyNumber" },
            new() { Name = "Requirement", Description = "A specific obligation or requirement.", Attributes = ["requirementNumber"], IdentifierAttribute = "requirementNumber" },
            new() { Name = "Contract", Description = "A contract or agreement with another party.", Attributes = ["contractNumber"], IdentifierAttribute = "contractNumber" },
            new() { Name = "BusinessProcess", Description = "A repeatable business process or procedure." }
        ],
        Relations =
        [
            new()
            {
                Predicate = "DEPENDS_ON",
                Description = "The subject cannot function or proceed without the object.",
                SubjectTypes = ["System", "Project", "BusinessProcess"],
                ObjectTypes = ["System"]
            },
            new()
            {
                Predicate = "INTEGRATES_WITH",
                Description = "The two systems exchange data or calls with each other.",
                SubjectTypes = ["System"],
                ObjectTypes = ["System"],
                Symmetric = true
            },
            new()
            {
                Predicate = "OWNED_BY",
                Description = "The object is accountable for the subject.",
                SubjectTypes = ["System", "Project", "Policy", "Contract", "BusinessProcess"],
                ObjectTypes = ["Team", "Person"]
            },
            new()
            {
                Predicate = "IMPLEMENTS",
                Description = "The subject puts the object into effect.",
                SubjectTypes = ["System", "Project", "BusinessProcess"],
                ObjectTypes = ["Requirement", "Policy"]
            },
            new()
            {
                Predicate = "SUPERSEDES",
                Description = "The subject replaces the object, which no longer applies.",
                SubjectTypes = ["Policy", "Requirement", "Contract"],
                ObjectTypes = ["Policy", "Requirement", "Contract"],
                RequireSameEntityType = true,
                Antisymmetric = true
            },
            new()
            {
                Predicate = "APPLIES_TO",
                Description = "The subject governs or constrains the object.",
                SubjectTypes = ["Policy", "Requirement", "Contract"],
                ObjectTypes = ["System", "Project", "Team", "BusinessProcess"]
            }
        ]
    };
}
