using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SharePointAgent.Persistence.Migrations;

[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20261016090000_RenameFindRelationshipsTool")]
public sealed class RenameFindRelationshipsTool : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        RenameTool(migrationBuilder, "find_related_documents", "find_relationships");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        RenameTool(migrationBuilder, "find_relationships", "find_related_documents");
    }

    private static void RenameTool(MigrationBuilder migrationBuilder, string previous, string current)
    {
        // Tools is a JSON string array. Match the complete quoted name, preserving other selections and null.
        foreach (var table in new[] { "AgentDefinitions", "SubAgentDefinitions" })
        {
            migrationBuilder.Sql($"""
                UPDATE [{table}]
                SET [Tools] = REPLACE([Tools], '"{previous}"', '"{current}"')
                WHERE [Tools] LIKE '%"{previous}"%';
                """);
        }
    }
}
