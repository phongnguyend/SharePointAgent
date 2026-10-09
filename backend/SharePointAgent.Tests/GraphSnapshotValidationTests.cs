using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class GraphSnapshotValidationTests
{
    private const string Tenant = "tenant-a";

    private static readonly string DocumentId = GraphDocumentIds.ForDriveItem("drive-1", "item-1");

    private static readonly string Revision = GraphDocumentIds.ForRevision("\"c:{A},3\"", "chunk=4000/400");

    private static readonly GraphOntology Ontology = GraphOntology.Create(GraphOntologyDefaults.CreateDefinition());

    private static readonly CanonicalGraphSnapshotValidator Validator = new(Ontology);

    private static GraphEntity Entity(string type, string name, string tenant = Tenant) =>
        new(tenant, GraphIds.ForEntity(tenant, type, GraphEntityKey.FromName(name)), type, name, [], new Dictionary<string, string>());

    private static EvidenceRef Evidence(int chunkNumber = 0) =>
        new(Tenant, DocumentId, Revision, GraphDocumentIds.ForChunk("drive-1", "item-1", chunkNumber),
            GraphDocumentIds.ForChunkContent($"chunk {chunkNumber}"), null, null, "permissions-hash");

    private static GraphAssertion Assertion(GraphEntity subject, string predicate, GraphEntity @object, EvidenceRef? evidence = null)
    {
        evidence ??= Evidence();
        var symmetric = Ontology.Relations.TryGetValue(predicate, out var relation) && relation.Symmetric;
        var id = GraphIds.ForAssertion(evidence, subject.EntityId, predicate, @object.EntityId, symmetric);
        return new GraphAssertion(Tenant, id, subject.EntityId, predicate, @object.EntityId, evidence, GraphAssertionStatus.Asserted, Ontology.Version, "x1");
    }

    private static CanonicalGraphSnapshot Snapshot(IReadOnlyList<GraphEntity> entities, IReadOnlyList<GraphAssertion> assertions) =>
        new(Tenant, DocumentId, Revision, CanonicalGraphSnapshot.CurrentSchemaVersion,
            new GraphExtractionProvenance("x1", "gpt-test", "prompt-1", Ontology.Version), entities, assertions,
            entities.Where(entity => entity is not null && entity.EntityId is not null)
                .Select((entity, index) => new EntityResolutionDecision($"0:e{index}", entity.EntityId, EntityResolutionMethod.NewEntity, []))
                .ToList());

    private static CanonicalGraphSnapshot ValidSnapshot()
    {
        var crm = Entity("System", "CRM");
        var erp = Entity("System", "ERP");
        var team = Entity("Team", "Sales Platform Team");
        return Snapshot([crm, erp, team], [Assertion(crm, "DEPENDS_ON", erp), Assertion(crm, "OWNED_BY", team)]);
    }

    private static void AssertRejected(CanonicalGraphSnapshot snapshot, string code, string? path = null)
    {
        var result = Validator.Validate(snapshot);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == code && (path is null || error.Path == path));
    }

    [Fact]
    public void AWellFormedSnapshotIsValid()
    {
        var result = Validator.Validate(ValidSnapshot());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AnEmptySnapshotIsValidSoADocumentWithoutRelationsCanStillBeRecorded()
    {
        Assert.True(Validator.Validate(Snapshot([], [])).IsValid);
    }

    [Fact]
    public void AMissingSnapshotIsReported()
    {
        Assert.False(Validator.Validate(null).IsValid);
    }

    [Fact]
    public void EntitiesFromAnotherTenantAreRejected()
    {
        var snapshot = ValidSnapshot();
        var foreign = Entity("System", "Billing", tenant: "tenant-b");

        AssertRejected(snapshot with { Entities = [.. snapshot.Entities, foreign] }, GraphValidationCodes.TenantMismatch, "entities[3].tenantId");
    }

    [Fact]
    public void AssertionsAndEvidenceFromAnotherTenantAreRejected()
    {
        var snapshot = ValidSnapshot();
        var assertion = snapshot.Assertions[0];

        AssertRejected(snapshot with { Assertions = [assertion with { TenantId = "tenant-b" }] }, GraphValidationCodes.TenantMismatch, "assertions[0].tenantId");
        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { TenantId = "tenant-b" } }] }, GraphValidationCodes.TenantMismatch, "assertions[0].evidence.tenantId");
    }

    [Fact]
    public void EvidenceMustCiteTheSnapshotsOwnDocumentRevision()
    {
        var snapshot = ValidSnapshot();
        var assertion = snapshot.Assertions[0];

        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { DocumentVersion = "rev:old" } }] }, GraphValidationCodes.EvidenceDocumentMismatch);
        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { DocumentId = "drive-1:other" } }] }, GraphValidationCodes.EvidenceDocumentMismatch);
    }

    [Fact]
    public void EvidenceMustNameAUsableChunkAndPlausibleLocation()
    {
        var snapshot = ValidSnapshot();
        var assertion = snapshot.Assertions[0];

        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { ChunkId = "" } }] }, GraphValidationCodes.InvalidIdentifier, "assertions[0].evidence.chunkId");
        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { PageNumber = 0 } }] }, GraphValidationCodes.EvidenceInvalid, "assertions[0].evidence.pageNumber");
        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { Section = new string('s', 513) } }] }, GraphValidationCodes.EvidenceInvalid, "assertions[0].evidence.section");
    }

    [Fact]
    public void EvidenceMustCarryAWellFormedChunkContentHash()
    {
        var snapshot = ValidSnapshot();
        var assertion = snapshot.Assertions[0];

        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { ChunkContentHash = "" } }] }, GraphValidationCodes.EvidenceInvalid, "assertions[0].evidence.chunkContentHash");
        AssertRejected(snapshot with { Assertions = [assertion with { Evidence = assertion.Evidence with { ChunkContentHash = "md5:abc" } }] }, GraphValidationCodes.EvidenceInvalid, "assertions[0].evidence.chunkContentHash");
    }

    [Fact]
    public void ResolutionsMustPointAtTheSnapshotsEntities()
    {
        var snapshot = ValidSnapshot();
        var outsider = GraphIds.ForEntity(Tenant, "System", GraphEntityKey.FromName("Elsewhere"));

        AssertRejected(snapshot with { Resolutions = [new EntityResolutionDecision("0:x", outsider, EntityResolutionMethod.ExactAlias, [])] }, GraphValidationCodes.ResolutionInvalid, "resolutions[0].entityId");
        AssertRejected(snapshot with { Resolutions = [.. snapshot.Resolutions, snapshot.Resolutions[0]] }, GraphValidationCodes.ResolutionInvalid, "resolutions[3].localRef");
        AssertRejected(snapshot with { Resolutions = null! }, GraphValidationCodes.Required, "resolutions");
    }

    [Fact]
    public void AmbiguousResolutionsRecordTheirCandidates()
    {
        var snapshot = ValidSnapshot();
        var candidates = new[] { GraphIds.ForEntity(Tenant, "System", "id:cmdb:1"), GraphIds.ForEntity(Tenant, "System", "id:cmdb:2") };
        var decision = new EntityResolutionDecision("0:e0", snapshot.Entities[0].EntityId, EntityResolutionMethod.Ambiguous, candidates);

        Assert.True(Validator.Validate(snapshot with { Resolutions = [decision] }).IsValid);
        AssertRejected(snapshot with { Resolutions = [decision with { CandidateEntityIds = ["bad/id"] }] }, GraphValidationCodes.ResolutionInvalid);
    }

    [Fact]
    public void AnAssertionWithoutEvidenceIsRejected()
    {
        var snapshot = ValidSnapshot();

        AssertRejected(snapshot with { Assertions = [snapshot.Assertions[0] with { Evidence = null! }] }, GraphValidationCodes.Required, "assertions[0].evidence");
    }

    [Fact]
    public void UnknownEntityTypesAndPredicatesAreRejected()
    {
        var snapshot = ValidSnapshot();
        var vendor = new GraphEntity(Tenant, GraphIds.ForEntity(Tenant, "Vendor", "name:acme"), "Vendor", "Acme", [], new Dictionary<string, string>());

        AssertRejected(snapshot with { Entities = [.. snapshot.Entities, vendor] }, GraphValidationCodes.UnknownEntityType, "entities[3].entityType");
        AssertRejected(snapshot with { Assertions = [snapshot.Assertions[0] with { Predicate = "MENTIONED_WITH" }] }, GraphValidationCodes.UnknownPredicate);
    }

    [Fact]
    public void RelationsMustFollowTheOntologysTypesAndDirection()
    {
        var crm = Entity("System", "CRM");
        var team = Entity("Team", "Sales Platform Team");

        AssertRejected(Snapshot([crm, team], [Assertion(team, "OWNED_BY", crm)]), GraphValidationCodes.RelationNotAllowed, "assertions[0]");
    }

    [Fact]
    public void SupersedingRequiresTheSameEntityType()
    {
        var policy = Entity("Policy", "Data Retention Policy");
        var contract = Entity("Contract", "Hosting Agreement");

        AssertRejected(Snapshot([policy, contract], [Assertion(policy, "SUPERSEDES", contract)]), GraphValidationCodes.RelationNotAllowed);
    }

    [Fact]
    public void SelfReferencesAreRejectedUnlessTheRelationAllowsThem()
    {
        var crm = Entity("System", "CRM");

        AssertRejected(Snapshot([crm], [Assertion(crm, "DEPENDS_ON", crm)]), GraphValidationCodes.SelfReference);
    }

    [Fact]
    public void SymmetricRelationsAreStoredInOneOrientationOnly()
    {
        var crm = Entity("System", "CRM");
        var erp = Entity("System", "ERP");
        var (first, second) = string.CompareOrdinal(crm.EntityId, erp.EntityId) < 0 ? (crm, erp) : (erp, crm);

        Assert.True(Validator.Validate(Snapshot([crm, erp], [Assertion(first, "INTEGRATES_WITH", second)])).IsValid);
        AssertRejected(Snapshot([crm, erp], [Assertion(second, "INTEGRATES_WITH", first)]), GraphValidationCodes.SymmetricOrientation);
    }

    [Fact]
    public void AssertionsMustReferToEntitiesInTheSnapshot()
    {
        var crm = Entity("System", "CRM");
        var erp = Entity("System", "ERP");

        AssertRejected(Snapshot([crm], [Assertion(crm, "DEPENDS_ON", erp)]), GraphValidationCodes.UnknownEntityReference, "assertions[0].objectEntityId");
    }

    [Fact]
    public void AssertionIdsMustBeTheDeterministicOnes()
    {
        var snapshot = ValidSnapshot();
        var assertion = snapshot.Assertions[0];

        AssertRejected(snapshot with { Assertions = [assertion with { AssertionId = "assertion:0000" }] }, GraphValidationCodes.AssertionIdMismatch);
        AssertRejected(snapshot with { Assertions = [assertion with { AssertionId = "edge-1" }] }, GraphValidationCodes.InvalidIdentifier);
    }

    [Fact]
    public void ChangingTheRelationWithoutTheIdIsDetected()
    {
        var snapshot = ValidSnapshot();
        var assertion = snapshot.Assertions[0];
        var other = Entity("System", "Billing");

        AssertRejected(snapshot with { Entities = [.. snapshot.Entities, other], Assertions = [assertion with { ObjectEntityId = other.EntityId }] }, GraphValidationCodes.AssertionIdMismatch);
    }

    [Fact]
    public void DuplicateExtractionsOfTheSameRelationAreRejected()
    {
        var snapshot = ValidSnapshot();

        AssertRejected(snapshot with { Assertions = [snapshot.Assertions[0], snapshot.Assertions[0]] }, GraphValidationCodes.DuplicateId, "assertions[1].assertionId");
    }

    [Fact]
    public void TheSameRelationFromDifferentChunksIsKeptAsSeparateAssertions()
    {
        var crm = Entity("System", "CRM");
        var erp = Entity("System", "ERP");

        var result = Validator.Validate(Snapshot([crm, erp], [Assertion(crm, "DEPENDS_ON", erp, Evidence(0)), Assertion(crm, "DEPENDS_ON", erp, Evidence(1))]));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ConflictingAssertionsCanCoexistAsDisputed()
    {
        var snapshot = ValidSnapshot();
        var disputed = snapshot.Assertions[0] with { Status = GraphAssertionStatus.Disputed };

        Assert.True(Validator.Validate(snapshot with { Assertions = [disputed, snapshot.Assertions[1]] }).IsValid);
    }

    [Fact]
    public void ASnapshotCannotContainRetractedAssertions()
    {
        var snapshot = ValidSnapshot();

        AssertRejected(snapshot with { Assertions = [snapshot.Assertions[0] with { Status = GraphAssertionStatus.Retracted }] }, GraphValidationCodes.StatusNotAllowed);
    }

    [Fact]
    public void DuplicateEntitiesAreRejected()
    {
        var snapshot = ValidSnapshot();

        AssertRejected(snapshot with { Entities = [.. snapshot.Entities, snapshot.Entities[0]] }, GraphValidationCodes.DuplicateId, "entities[3].entityId");
    }

    [Fact]
    public void EntityIdsMustCarryTheirTypesPrefix()
    {
        var snapshot = ValidSnapshot();
        var mislabelled = snapshot.Entities[2] with { EntityType = "Person" };

        AssertRejected(snapshot with { Entities = [snapshot.Entities[0], snapshot.Entities[1], mislabelled] }, GraphValidationCodes.EntityIdTypeMismatch);
    }

    [Fact]
    public void OnlyTheOntologysAttributesAreAccepted()
    {
        var snapshot = ValidSnapshot();
        var withVendor = snapshot.Entities[0] with { Attributes = new Dictionary<string, string> { ["vendor"] = "Contoso" } };
        var withText = snapshot.Entities[0] with { Attributes = new Dictionary<string, string> { ["notes"] = "copied paragraph" } };

        Assert.True(Validator.Validate(snapshot with { Entities = [withVendor, snapshot.Entities[1], snapshot.Entities[2]] }).IsValid);
        AssertRejected(snapshot with { Entities = [withText, snapshot.Entities[1], snapshot.Entities[2]] }, GraphValidationCodes.AttributeNotAllowed);
    }

    [Fact]
    public void AliasesThatDifferOnlyByNormalizationAreDuplicates()
    {
        var snapshot = ValidSnapshot();
        var aliased = snapshot.Entities[0] with { Aliases = ["Customer Relationship Management", " customer  relationship management"] };

        AssertRejected(snapshot with { Entities = [aliased, snapshot.Entities[1], snapshot.Entities[2]] }, GraphValidationCodes.DuplicateAlias, "entities[0].aliases[1]");
    }

    [Fact]
    public void VersionsMustMatchTheOntologyAndExtraction()
    {
        var snapshot = ValidSnapshot();

        AssertRejected(snapshot with { Extraction = snapshot.Extraction with { OntologyVersion = "v0" } }, GraphValidationCodes.OntologyVersionMismatch, "extraction.ontologyVersion");
        AssertRejected(snapshot with { Assertions = [snapshot.Assertions[0] with { OntologyVersion = "v0" }] }, GraphValidationCodes.OntologyVersionMismatch);
        AssertRejected(snapshot with { Assertions = [snapshot.Assertions[0] with { ExtractionVersion = "x0" }] }, GraphValidationCodes.ExtractionVersionMismatch);
        AssertRejected(snapshot with { SchemaVersion = "2" }, GraphValidationCodes.SchemaVersionUnsupported);
    }

    [Fact]
    public void ProvenanceIsRequired()
    {
        var snapshot = ValidSnapshot();

        AssertRejected(snapshot with { Extraction = null! }, GraphValidationCodes.Required, "extraction");
        AssertRejected(snapshot with { Extraction = snapshot.Extraction with { ModelId = " " } }, GraphValidationCodes.Required, "extraction.modelId");
        AssertRejected(snapshot with { Extraction = snapshot.Extraction with { PromptVersion = "" } }, GraphValidationCodes.Required, "extraction.promptVersion");
    }

    [Fact]
    public void TheDocumentIdMustIdentifyADriveItem()
    {
        AssertRejected(ValidSnapshot() with { DocumentId = "just-an-item" }, GraphValidationCodes.InvalidIdentifier, "documentId");
    }

    [Fact]
    public void OversizedSnapshotsAreRejectedWithoutInspectingEveryItem()
    {
        var validator = new CanonicalGraphSnapshotValidator(Ontology, new GraphSnapshotLimits(MaxEntities: 2));
        var snapshot = ValidSnapshot() with { Entities = [.. ValidSnapshot().Entities, null!] };

        var result = validator.Validate(snapshot);

        var error = Assert.Single(result.Errors);
        Assert.Equal(GraphValidationCodes.LimitExceeded, error.Code);
    }

    [Fact]
    public void TheErrorReportIsBounded()
    {
        var validator = new CanonicalGraphSnapshotValidator(Ontology, new GraphSnapshotLimits(MaxErrors: 5));
        var entities = Enumerable.Range(0, 50).Select(index => new GraphEntity("tenant-b", "", "Unknown", "", null!, null!)).ToList();

        var result = validator.Validate(Snapshot(entities, []));

        Assert.Equal(5, result.Errors.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void NullMembersFromDeserializedModelOutputAreReportedRatherThanThrown()
    {
        var entity = new GraphEntity(Tenant, null!, null!, null!, null!, null!);
        var assertion = new GraphAssertion(Tenant, null!, null!, null!, null!, null!, GraphAssertionStatus.Asserted, null!, null!);

        var result = Validator.Validate(Snapshot([entity], [assertion]) with { Extraction = null! });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ErrorsNeverEchoExtractedContent()
    {
        const string secret = "Project Nightingale acquisition";
        var snapshot = ValidSnapshot();
        var leaky = snapshot.Entities[0] with
        {
            EntityType = secret,
            CanonicalName = secret + new string('x', 300),
            Aliases = [secret, secret],
            Attributes = new Dictionary<string, string> { [secret] = secret }
        };
        var assertion = snapshot.Assertions[0] with { Predicate = secret, Evidence = snapshot.Assertions[0].Evidence with { Section = secret + new string('x', 600) } };

        var result = Validator.Validate(snapshot with { Entities = [leaky, snapshot.Entities[1]], Assertions = [assertion] });

        Assert.False(result.IsValid);
        Assert.All(result.Errors, error =>
        {
            Assert.DoesNotContain("Nightingale", error.Message);
            Assert.DoesNotContain("Nightingale", error.Path);
        });
    }
}
