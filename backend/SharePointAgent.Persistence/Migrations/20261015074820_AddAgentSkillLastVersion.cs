using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSkillLastVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastVersion",
                table: "AgentSkillDefinitions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            // Defer compilation until the column exists, including in idempotent scripts.
            // CurrentVersion may also have been renamed when an applied migration is skipped.
            migrationBuilder.Sql("""
                EXEC(N'UPDATE d SET [LastVersion] = CASE WHEN v.[MaxVersion] > d.[CurrentVersion] THEN v.[MaxVersion] ELSE d.[CurrentVersion] END
                FROM [AgentSkillDefinitions] d
                OUTER APPLY (SELECT MAX([Version]) AS [MaxVersion] FROM [AgentSkillVersions] WHERE [AgentSkillDefinitionId] = d.[Id]) v;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastVersion",
                table: "AgentSkillDefinitions");
        }
    }
}
