using Microsoft.Extensions.Configuration;
using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class GraphOntologyTests
{
    [Fact]
    public void TheDefaultOntologyIsValid()
    {
        var ontology = GraphOntology.Create(GraphOntologyDefaults.CreateDefinition());

        Assert.Equal(GraphOntologyDefaults.Version, ontology.Version);
        Assert.Equal(8, ontology.EntityTypes.Count);
        Assert.Equal(6, ontology.Relations.Count);
    }

    [Theory]
    [InlineData("System", "DEPENDS_ON", "System", true)]
    [InlineData("System", "OWNED_BY", "Team", true)]
    [InlineData("Policy", "APPLIES_TO", "System", true)]
    [InlineData("Team", "DEPENDS_ON", "System", false)]
    [InlineData("System", "OWNED_BY", "System", false)]
    [InlineData("Policy", "SUPERSEDES", "Policy", true)]
    [InlineData("Policy", "SUPERSEDES", "Contract", false)]
    [InlineData("System", "MENTIONED_WITH", "System", false)]
    public void AllowedTriplesFollowTheDefinitionAndItsDirection(string subjectType, string predicate, string objectType, bool allowed)
    {
        var ontology = GraphOntology.Create(GraphOntologyDefaults.CreateDefinition());

        Assert.Equal(allowed, ontology.IsAllowedTriple(subjectType, predicate, objectType));
    }

    [Fact]
    public void NamesAreMatchedExactly()
    {
        var ontology = GraphOntology.Create(GraphOntologyDefaults.CreateDefinition());

        Assert.False(ontology.IsEntityType("system"));
        Assert.False(ontology.TryGetRelation("depends_on", out _));
    }

    [Fact]
    public void AnInvalidDefinitionIsRejectedWithEveryProblem()
    {
        var definition = new GraphOntologyDefinition
        {
            Version = "",
            EntityTypes =
            [
                new() { Name = "System" },
                new() { Name = "system" },
                new() { Name = "bad name" },
                new() { Name = "Team", Attributes = ["Key", "ok", "ok"] }
            ],
            Relations =
            [
                new() { Predicate = "depends on", SubjectTypes = ["System"], ObjectTypes = ["System"] },
                new() { Predicate = "OWNED_BY", SubjectTypes = ["System"], ObjectTypes = ["Person"] },
                new() { Predicate = "OWNED_BY", SubjectTypes = ["System"], ObjectTypes = ["Team"] },
                new() { Predicate = "LINKS", SubjectTypes = ["System"], ObjectTypes = ["Team"], Symmetric = true },
                new() { Predicate = "REPLACES", SubjectTypes = ["System"], ObjectTypes = ["Team"], RequireSameEntityType = true },
                new() { Predicate = "EMPTY", SubjectTypes = [], ObjectTypes = ["Team"] }
            ]
        };

        var exception = Assert.Throws<GraphOntologyDefinitionException>(() => GraphOntology.Create(definition));

        var paths = exception.Errors.Select(error => error.Path).ToList();
        Assert.Contains("version", paths);
        Assert.Contains("entityTypes[1].name", paths);
        Assert.Contains("entityTypes[2].name", paths);
        Assert.Contains("entityTypes[3].attributes[0]", paths);
        Assert.Contains("entityTypes[3].attributes[2]", paths);
        Assert.Contains("relations[0].predicate", paths);
        Assert.Contains("relations[1].objectTypes[0]", paths);
        Assert.Contains("relations[2].predicate", paths);
        Assert.Contains("relations[3]", paths);
        Assert.Contains("relations[4]", paths);
        Assert.Contains("relations[5].subjectTypes", paths);
        Assert.All(exception.Errors, error => Assert.Equal(GraphValidationCodes.OntologyInvalid, error.Code));
    }

    [Fact]
    public void IdentifierAttributesMustBeDeclaredAttributes()
    {
        var definition = GraphOntologyDefaults.CreateDefinition();
        definition.EntityTypes[0].IdentifierAttribute = "serialNumber";

        var exception = Assert.Throws<GraphOntologyDefinitionException>(() => GraphOntology.Create(definition));

        Assert.Contains(exception.Errors, error => error.Path == "entityTypes[0].identifierAttribute");
    }

    [Fact]
    public void ARelationCannotBeBothSymmetricAndAntisymmetric()
    {
        var definition = GraphOntologyDefaults.CreateDefinition();
        definition.Relations.Single(relation => relation.Predicate == "INTEGRATES_WITH").Antisymmetric = true;

        Assert.Throws<GraphOntologyDefinitionException>(() => GraphOntology.Create(definition));
    }

    [Fact]
    public void TheDefaultOntologyMarksAuthoritativeIdentifiersAndAntisymmetry()
    {
        var ontology = GraphOntology.Create(GraphOntologyDefaults.CreateDefinition());

        Assert.Equal("policyNumber", ontology.EntityTypes["Policy"].IdentifierAttribute);
        Assert.Null(ontology.EntityTypes["System"].IdentifierAttribute);
        Assert.True(ontology.Relations["SUPERSEDES"].Antisymmetric);
    }

    [Fact]
    public void AMissingDefinitionIsReportedRatherThanThrown()
    {
        Assert.Single(GraphOntology.ValidateDefinition(null));
    }

    [Fact]
    public void ADefinitionBoundFromConfigurationCompiles()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ontology:Version"] = "corpus-2",
                ["Ontology:EntityTypes:0:Name"] = "System",
                ["Ontology:EntityTypes:0:Attributes:0"] = "vendor",
                ["Ontology:EntityTypes:1:Name"] = "Team",
                ["Ontology:Relations:0:Predicate"] = "OWNED_BY",
                ["Ontology:Relations:0:SubjectTypes:0"] = "System",
                ["Ontology:Relations:0:ObjectTypes:0"] = "Team"
            })
            .Build();
        var definition = new GraphOntologyDefinition();
        configuration.GetSection("Ontology").Bind(definition);

        var ontology = GraphOntology.Create(definition);

        Assert.Equal("corpus-2", ontology.Version);
        Assert.True(ontology.IsAllowedTriple("System", "OWNED_BY", "Team"));
        Assert.Contains("vendor", ontology.EntityTypes["System"].Attributes);
    }
}
