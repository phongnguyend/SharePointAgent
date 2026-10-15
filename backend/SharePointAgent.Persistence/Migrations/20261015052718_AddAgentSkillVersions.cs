using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSkillVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentSkillFiles_AgentSkillDefinitionId_Path",
                table: "AgentSkillFiles");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "AgentSkillFiles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CurrentVersion",
                table: "AgentSkillDefinitions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "AgentSkillVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    AgentSkillDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkillVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSkillVersions_AgentSkillDefinitions_AgentSkillDefinitionId",
                        column: x => x.AgentSkillDefinitionId,
                        principalTable: "AgentSkillDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO [AgentSkillVersions] ([AgentSkillDefinitionId], [Version], [Description], [ContentHash], [CreatedAtUtc])
                SELECT [Id], 1, [Description], [ContentHash], [UpdatedAtUtc] FROM [AgentSkillDefinitions];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkillFiles_AgentSkillDefinitionId_Version_Path",
                table: "AgentSkillFiles",
                columns: new[] { "AgentSkillDefinitionId", "Version", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkillVersions_AgentSkillDefinitionId_Version",
                table: "AgentSkillVersions",
                columns: new[] { "AgentSkillDefinitionId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [AgentSkillFiles] f INNER JOIN [AgentSkillDefinitions] d ON d.[Id] = f.[AgentSkillDefinitionId] WHERE f.[Version] <> d.[CurrentVersion])
                    THROW 50000, 'Delete historical skill versions through the application before reverting this migration.', 1;
                """);

            migrationBuilder.DropTable(
                name: "AgentSkillVersions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSkillFiles_AgentSkillDefinitionId_Version_Path",
                table: "AgentSkillFiles");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "AgentSkillFiles");

            migrationBuilder.DropColumn(
                name: "CurrentVersion",
                table: "AgentSkillDefinitions");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkillFiles_AgentSkillDefinitionId_Path",
                table: "AgentSkillFiles",
                columns: new[] { "AgentSkillDefinitionId", "Path" },
                unique: true);
        }
    }
}
