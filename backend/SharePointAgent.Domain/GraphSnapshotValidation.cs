namespace SharePointAgent.Domain;

/// <summary>
/// One problem with an ontology definition or a graph snapshot. For snapshots, <see cref="Path"/> and
/// <see cref="Message"/> name positions and rules only, never extracted values, so validation failures can
/// be logged and dead-lettered without copying document content or entity names out of the document's
/// permission boundary.
/// </summary>
public sealed record GraphValidationError(string Code, string Path, string Message);

public sealed record GraphValidationResult(IReadOnlyList<GraphValidationError> Errors, bool Truncated)
{
    public bool IsValid => Errors.Count == 0;
}

public static class GraphValidationCodes
{
    public const string OntologyInvalid = "ontology.invalid";

    public const string Required = "required";

    public const string InvalidIdentifier = "identifier.invalid";

    public const string TenantMismatch = "tenant.mismatch";

    public const string SchemaVersionUnsupported = "schema.version";

    public const string OntologyVersionMismatch = "ontology.version";

    public const string ExtractionVersionMismatch = "extraction.version";

    public const string LimitExceeded = "limit.exceeded";

    public const string DuplicateId = "id.duplicate";

    public const string UnknownEntityType = "entityType.unknown";

    public const string EntityIdTypeMismatch = "entity.idType";

    public const string DuplicateAlias = "entity.aliasDuplicate";

    public const string AttributeNotAllowed = "entity.attribute";

    public const string UnknownPredicate = "predicate.unknown";

    public const string UnknownEntityReference = "assertion.entityReference";

    public const string RelationNotAllowed = "assertion.relation";

    public const string SelfReference = "assertion.selfReference";

    public const string SymmetricOrientation = "assertion.symmetricOrientation";

    public const string AssertionIdMismatch = "assertion.id";

    public const string StatusNotAllowed = "assertion.status";

    public const string EvidenceDocumentMismatch = "evidence.document";

    public const string EvidenceInvalid = "evidence.invalid";

    public const string ResolutionInvalid = "resolution.invalid";
}

/// <summary>
/// Bounds on one document's snapshot. They limit how much an extraction can write for one document and how
/// much a malformed or adversarial model response can cost to validate, store, and traverse.
/// </summary>
public sealed record GraphSnapshotLimits(
    int MaxEntities = 500,
    int MaxAssertions = 1000,
    int MaxNameLength = 256,
    int MaxAliasesPerEntity = 20,
    int MaxAttributesPerEntity = 16,
    int MaxAttributeValueLength = 256,
    int MaxSectionLength = 512,
    int MaxVersionLength = 128,
    int MaxErrors = 100,
    int MaxResolutions = 2000,
    int MaxResolutionCandidates = 10);

/// <summary>
/// Checks a resolved snapshot against the ontology and the provenance rules before it is archived or
/// projected. Snapshots usually come from model output deserialized from JSON, so the validator tolerates
/// nulls in non-nullable members and reports them instead of throwing.
/// <para>
/// A snapshot describes one document revision of one tenant, and everything in it must say so: every
/// entity, assertion, and evidence reference carries the snapshot's tenant, every assertion is backed by a
/// chunk of the snapshot's own document revision, and every relation is a triple the ontology allows.
/// </para>
/// </summary>
public sealed class CanonicalGraphSnapshotValidator(GraphOntology ontology, GraphSnapshotLimits? limits = null)
{
    private readonly GraphOntology _ontology = ontology ?? throw new ArgumentNullException(nameof(ontology));

    private readonly GraphSnapshotLimits _limits = limits ?? new GraphSnapshotLimits();

    public GraphValidationResult Validate(CanonicalGraphSnapshot? snapshot)
    {
        var errors = new ErrorList(_limits.MaxErrors);
        if (snapshot is null)
        {
            errors.Add(GraphValidationCodes.Required, "", "The snapshot is missing.");
            return errors.ToResult();
        }

        ValidateHeader(snapshot, errors);

        var entities = snapshot.Entities;
        var assertions = snapshot.Assertions;
        if (entities is null)
        {
            errors.Add(GraphValidationCodes.Required, "entities", "The entity list is missing.");
        }
        else if (entities.Count > _limits.MaxEntities)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, "entities", $"A snapshot may contain at most {_limits.MaxEntities} entities.");
        }

        if (assertions is null)
        {
            errors.Add(GraphValidationCodes.Required, "assertions", "The assertion list is missing.");
        }
        else if (assertions.Count > _limits.MaxAssertions)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, "assertions", $"A snapshot may contain at most {_limits.MaxAssertions} assertions.");
        }

        // An oversized or structurally broken snapshot is rejected as a whole rather than inspected item by item.
        if (errors.Count > 0 && (entities is null || assertions is null || entities.Count > _limits.MaxEntities || assertions.Count > _limits.MaxAssertions))
        {
            return errors.ToResult();
        }

        var entityTypes = ValidateEntities(snapshot, entities!, errors);
        ValidateAssertions(snapshot, assertions!, entityTypes, errors);
        ValidateResolutions(snapshot.Resolutions, entityTypes, errors);
        return errors.ToResult();
    }

    private void ValidateResolutions(IReadOnlyList<EntityResolutionDecision>? resolutions, Dictionary<string, string> entityTypes, ErrorList errors)
    {
        if (resolutions is null)
        {
            errors.Add(GraphValidationCodes.Required, "resolutions", "The resolution list is missing.");
            return;
        }

        if (resolutions.Count > _limits.MaxResolutions)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, "resolutions", $"A snapshot may contain at most {_limits.MaxResolutions} resolutions.");
            return;
        }

        var localRefs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < resolutions.Count; index++)
        {
            var path = $"resolutions[{index}]";
            var resolution = resolutions[index];
            if (resolution is null)
            {
                errors.Add(GraphValidationCodes.Required, path, "The resolution is missing.");
                continue;
            }

            if (!GraphIds.IsStorageSafe(resolution.LocalRef) || resolution.LocalRef.Length > 128 || !localRefs.Add(resolution.LocalRef))
            {
                errors.Add(GraphValidationCodes.ResolutionInvalid, $"{path}.localRef", "The local reference is missing, too long, or repeated.");
            }

            if (resolution.EntityId is null || !entityTypes.ContainsKey(resolution.EntityId))
            {
                errors.Add(GraphValidationCodes.ResolutionInvalid, $"{path}.entityId", "The resolution must name a valid entity of this snapshot.");
            }

            if (!Enum.IsDefined(resolution.Method))
            {
                errors.Add(GraphValidationCodes.ResolutionInvalid, $"{path}.method", "The resolution method is not recognized.");
            }

            var candidates = resolution.CandidateEntityIds;
            if (candidates is null || candidates.Count > _limits.MaxResolutionCandidates || candidates.Any(candidate => !GraphIds.IsStorageSafe(candidate)))
            {
                errors.Add(GraphValidationCodes.ResolutionInvalid, $"{path}.candidateEntityIds", $"Candidates must be at most {_limits.MaxResolutionCandidates} valid entity IDs.");
            }
        }
    }

    private void ValidateHeader(CanonicalGraphSnapshot snapshot, ErrorList errors)
    {
        if (!GraphIds.IsStorageSafe(snapshot.TenantId))
        {
            errors.Add(GraphValidationCodes.InvalidIdentifier, "tenantId", "The tenant ID is missing or not a valid key.");
        }

        if (!GraphDocumentIds.TryParseDriveItem(snapshot.DocumentId, out _, out _) || snapshot.DocumentId.Length > GraphIds.MaxIdLength)
        {
            errors.Add(GraphValidationCodes.InvalidIdentifier, "documentId", "The document ID must be a drive ID and an item ID separated by ':'.");
        }

        ValidateVersion(snapshot.DocumentVersion, "documentVersion", errors);
        if (snapshot.SchemaVersion != CanonicalGraphSnapshot.CurrentSchemaVersion)
        {
            errors.Add(GraphValidationCodes.SchemaVersionUnsupported, "schemaVersion", $"Only schema version {CanonicalGraphSnapshot.CurrentSchemaVersion} is supported.");
        }

        var extraction = snapshot.Extraction;
        if (extraction is null)
        {
            errors.Add(GraphValidationCodes.Required, "extraction", "The extraction provenance is missing.");
            return;
        }

        ValidateVersion(extraction.ExtractionVersion, "extraction.extractionVersion", errors);
        ValidateVersion(extraction.ModelId, "extraction.modelId", errors);
        ValidateVersion(extraction.PromptVersion, "extraction.promptVersion", errors);
        if (extraction.OntologyVersion != _ontology.Version)
        {
            errors.Add(GraphValidationCodes.OntologyVersionMismatch, "extraction.ontologyVersion", $"The snapshot must use ontology version {_ontology.Version}.");
        }
    }

    private Dictionary<string, string> ValidateEntities(CanonicalGraphSnapshot snapshot, IReadOnlyList<GraphEntity> entities, ErrorList errors)
    {
        // Entity ID to type, for the assertion checks. Only well-formed entities are recorded, so an assertion
        // that refers to a rejected entity is reported as well.
        var entityTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entities.Count; index++)
        {
            var path = $"entities[{index}]";
            var entity = entities[index];
            if (entity is null)
            {
                errors.Add(GraphValidationCodes.Required, path, "The entity is missing.");
                continue;
            }

            var valid = true;
            if (entity.TenantId != snapshot.TenantId)
            {
                errors.Add(GraphValidationCodes.TenantMismatch, $"{path}.tenantId", "The entity belongs to a different tenant than the snapshot.");
                valid = false;
            }

            if (!GraphIds.IsStorageSafe(entity.EntityId))
            {
                errors.Add(GraphValidationCodes.InvalidIdentifier, $"{path}.entityId", "The entity ID is missing or not a valid key.");
                valid = false;
            }
            else if (!seen.Add(entity.EntityId))
            {
                errors.Add(GraphValidationCodes.DuplicateId, $"{path}.entityId", "The entity ID appears more than once.");
                valid = false;
            }

            if (!_ontology.IsEntityType(entity.EntityType))
            {
                errors.Add(GraphValidationCodes.UnknownEntityType, $"{path}.entityType", "The entity type is not in the ontology.");
                valid = false;
            }
            else if (entity.EntityId is not null && !entity.EntityId.StartsWith(GraphIds.EntityPrefix(entity.EntityType), StringComparison.Ordinal))
            {
                errors.Add(GraphValidationCodes.EntityIdTypeMismatch, $"{path}.entityId", "The entity ID does not carry the entity type's prefix.");
                valid = false;
            }

            ValidateName(entity.CanonicalName, $"{path}.canonicalName", errors);
            ValidateAliases(entity.Aliases, $"{path}.aliases", errors);
            if (_ontology.IsEntityType(entity.EntityType))
            {
                ValidateAttributes(entity.Attributes, _ontology.EntityTypes[entity.EntityType].Attributes, $"{path}.attributes", errors);
            }

            if (valid && entity.EntityId is not null && entity.EntityType is not null)
            {
                entityTypes[entity.EntityId] = entity.EntityType;
            }
        }
        return entityTypes;
    }

    private void ValidateName(string? name, string path, ErrorList errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(GraphValidationCodes.Required, path, "The name is missing.");
        }
        else if (name.Length > _limits.MaxNameLength)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, path, $"A name may be at most {_limits.MaxNameLength} characters.");
        }
    }

    private void ValidateAliases(IReadOnlyList<string>? aliases, string path, ErrorList errors)
    {
        if (aliases is null)
        {
            errors.Add(GraphValidationCodes.Required, path, "The alias list is missing.");
            return;
        }

        if (aliases.Count > _limits.MaxAliasesPerEntity)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, path, $"An entity may have at most {_limits.MaxAliasesPerEntity} aliases.");
            return;
        }

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < aliases.Count; index++)
        {
            var alias = aliases[index];
            ValidateName(alias, $"{path}[{index}]", errors);
            if (!string.IsNullOrWhiteSpace(alias) && !normalized.Add(GraphNormalization.NormalizeName(alias)))
            {
                errors.Add(GraphValidationCodes.DuplicateAlias, $"{path}[{index}]", "The alias duplicates another alias after normalization.");
            }
        }
    }

    private void ValidateAttributes(IReadOnlyDictionary<string, string>? attributes, IReadOnlySet<string> allowed, string path, ErrorList errors)
    {
        if (attributes is null)
        {
            errors.Add(GraphValidationCodes.Required, path, "The attribute map is missing.");
            return;
        }

        if (attributes.Count > _limits.MaxAttributesPerEntity)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, path, $"An entity may have at most {_limits.MaxAttributesPerEntity} attributes.");
            return;
        }

        // Attribute keys come from the model, so they are reported by position rather than by value.
        var position = 0;
        foreach (var (key, value) in attributes)
        {
            if (!allowed.Contains(key))
            {
                errors.Add(GraphValidationCodes.AttributeNotAllowed, $"{path}[{position}]", "The attribute is not allowed for the entity type.");
            }
            else if (string.IsNullOrWhiteSpace(value) || value.Length > _limits.MaxAttributeValueLength)
            {
                errors.Add(GraphValidationCodes.LimitExceeded, $"{path}[{position}]", $"An attribute value must be non-blank and at most {_limits.MaxAttributeValueLength} characters.");
            }
            position++;
        }
    }

    private void ValidateAssertions(
        CanonicalGraphSnapshot snapshot, IReadOnlyList<GraphAssertion> assertions, Dictionary<string, string> entityTypes, ErrorList errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < assertions.Count; index++)
        {
            var path = $"assertions[{index}]";
            var assertion = assertions[index];
            if (assertion is null)
            {
                errors.Add(GraphValidationCodes.Required, path, "The assertion is missing.");
                continue;
            }

            if (assertion.TenantId != snapshot.TenantId)
            {
                errors.Add(GraphValidationCodes.TenantMismatch, $"{path}.tenantId", "The assertion belongs to a different tenant than the snapshot.");
            }

            if (assertion.Status is not (GraphAssertionStatus.Asserted or GraphAssertionStatus.Disputed))
            {
                // Retraction is a change to a stored assertion, made when its document changes or is deleted; an
                // extraction never produces one.
                errors.Add(GraphValidationCodes.StatusNotAllowed, $"{path}.status", "A snapshot may contain only asserted or disputed assertions.");
            }

            if (assertion.OntologyVersion != _ontology.Version)
            {
                errors.Add(GraphValidationCodes.OntologyVersionMismatch, $"{path}.ontologyVersion", $"The assertion must use ontology version {_ontology.Version}.");
            }

            if (snapshot.Extraction is not null && assertion.ExtractionVersion != snapshot.Extraction.ExtractionVersion)
            {
                errors.Add(GraphValidationCodes.ExtractionVersionMismatch, $"{path}.extractionVersion", "The assertion's extraction version differs from the snapshot's.");
            }

            var evidenceValid = ValidateEvidence(snapshot, assertion.Evidence, $"{path}.evidence", errors);
            var relationValid = ValidateRelation(assertion, entityTypes, path, errors);

            if (!GraphIds.IsStorageSafe(assertion.AssertionId) || !GraphIds.IsAssertionId(assertion.AssertionId))
            {
                errors.Add(GraphValidationCodes.InvalidIdentifier, $"{path}.assertionId", "The assertion ID is missing or not a valid assertion key.");
            }
            else if (!seen.Add(assertion.AssertionId))
            {
                errors.Add(GraphValidationCodes.DuplicateId, $"{path}.assertionId", "The assertion ID appears more than once.");
            }
            else if (evidenceValid && relationValid)
            {
                var symmetric = _ontology.Relations[assertion.Predicate].Symmetric;
                var expected = GraphIds.ForAssertion(assertion.Evidence, assertion.SubjectEntityId, assertion.Predicate, assertion.ObjectEntityId, symmetric);
                if (assertion.AssertionId != expected)
                {
                    errors.Add(GraphValidationCodes.AssertionIdMismatch, $"{path}.assertionId", "The assertion ID is not the one derived from its evidence and relation.");
                }
            }
        }
    }

    private bool ValidateRelation(GraphAssertion assertion, Dictionary<string, string> entityTypes, string path, ErrorList errors)
    {
        string? subjectType = null;
        string? objectType = null;
        var subjectKnown = assertion.SubjectEntityId is not null && entityTypes.TryGetValue(assertion.SubjectEntityId, out subjectType);
        var objectKnown = assertion.ObjectEntityId is not null && entityTypes.TryGetValue(assertion.ObjectEntityId, out objectType);
        if (!subjectKnown)
        {
            errors.Add(GraphValidationCodes.UnknownEntityReference, $"{path}.subjectEntityId", "The subject is not a valid entity of this snapshot.");
        }

        if (!objectKnown)
        {
            errors.Add(GraphValidationCodes.UnknownEntityReference, $"{path}.objectEntityId", "The object is not a valid entity of this snapshot.");
        }

        if (!_ontology.TryGetRelation(assertion.Predicate, out var relation))
        {
            errors.Add(GraphValidationCodes.UnknownPredicate, $"{path}.predicate", "The predicate is not in the ontology.");
            return false;
        }

        if (subjectType is null || objectType is null)
        {
            return false;
        }

        var valid = true;
        if (!_ontology.IsAllowedTriple(subjectType, relation.Predicate, objectType))
        {
            errors.Add(GraphValidationCodes.RelationNotAllowed, path, $"The ontology does not allow these entity types for {relation.Predicate}.");
            valid = false;
        }

        var selfReference = assertion.SubjectEntityId == assertion.ObjectEntityId;
        if (selfReference && !relation.AllowSelfReference)
        {
            errors.Add(GraphValidationCodes.SelfReference, path, $"{relation.Predicate} may not relate an entity to itself.");
            valid = false;
        }

        // A symmetric relation is stored in one orientation only, so the same pair cannot be stored twice.
        if (relation.Symmetric && string.CompareOrdinal(assertion.SubjectEntityId, assertion.ObjectEntityId) > 0)
        {
            errors.Add(GraphValidationCodes.SymmetricOrientation, path, $"{relation.Predicate} is symmetric, so its subject must be the entity whose ID sorts first.");
            valid = false;
        }
        return valid;
    }

    private bool ValidateEvidence(CanonicalGraphSnapshot snapshot, EvidenceRef? evidence, string path, ErrorList errors)
    {
        if (evidence is null)
        {
            errors.Add(GraphValidationCodes.Required, path, "The evidence is missing.");
            return false;
        }

        var valid = true;
        if (evidence.TenantId != snapshot.TenantId)
        {
            errors.Add(GraphValidationCodes.TenantMismatch, $"{path}.tenantId", "The evidence belongs to a different tenant than the snapshot.");
            valid = false;
        }

        if (evidence.DocumentId != snapshot.DocumentId || evidence.DocumentVersion != snapshot.DocumentVersion)
        {
            errors.Add(GraphValidationCodes.EvidenceDocumentMismatch, path, "The evidence must cite the snapshot's own document revision.");
            valid = false;
        }

        if (!GraphIds.IsStorageSafe(evidence.ChunkId))
        {
            errors.Add(GraphValidationCodes.InvalidIdentifier, $"{path}.chunkId", "The chunk ID is missing or not a valid key.");
            valid = false;
        }

        if (!GraphDocumentIds.IsChunkContentHash(evidence.ChunkContentHash))
        {
            errors.Add(GraphValidationCodes.EvidenceInvalid, $"{path}.chunkContentHash", "The chunk content hash is missing or malformed.");
            valid = false;
        }

        if (evidence.PageNumber is < 1)
        {
            errors.Add(GraphValidationCodes.EvidenceInvalid, $"{path}.pageNumber", "A page number must be at least 1.");
            valid = false;
        }

        if (evidence.Section is not null && (evidence.Section.Length == 0 || evidence.Section.Length > _limits.MaxSectionLength))
        {
            errors.Add(GraphValidationCodes.EvidenceInvalid, $"{path}.section", $"A section must be non-empty and at most {_limits.MaxSectionLength} characters.");
            valid = false;
        }

        if (evidence.AccessScopeRef is not null && !GraphIds.IsStorageSafe(evidence.AccessScopeRef))
        {
            errors.Add(GraphValidationCodes.EvidenceInvalid, $"{path}.accessScopeRef", "The access scope reference is not a valid key.");
            valid = false;
        }
        return valid;
    }

    private void ValidateVersion(string? value, string path, ErrorList errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(GraphValidationCodes.Required, path, "The value is missing.");
        }
        else if (value.Length > _limits.MaxVersionLength)
        {
            errors.Add(GraphValidationCodes.LimitExceeded, path, $"The value may be at most {_limits.MaxVersionLength} characters.");
        }
    }

    /// <summary>Stops collecting after a fixed number of errors so a hostile snapshot cannot produce an unbounded report.</summary>
    private sealed class ErrorList(int maximum)
    {
        private readonly List<GraphValidationError> _errors = [];

        private bool _truncated;

        public int Count => _errors.Count;

        public void Add(string code, string path, string message)
        {
            if (_errors.Count >= maximum)
            {
                _truncated = true;
                return;
            }
            _errors.Add(new GraphValidationError(code, path, message));
        }

        public GraphValidationResult ToResult() => new(_errors, _truncated);
    }
}
