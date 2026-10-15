using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameAgentSkillCurrentVersionToActiveVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CurrentVersion",
                table: "AgentSkillDefinitions",
                newName: "ActiveVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ActiveVersion",
                table: "AgentSkillDefinitions",
                newName: "CurrentVersion");
        }
    }
}
