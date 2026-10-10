using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackIsolatedEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DynamicSessionId",
                table: "ChatWorkspaces",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxId",
                table: "ChatWorkspaces",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DynamicSessionId",
                table: "ChatConversations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxId",
                table: "ChatConversations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DynamicSessionId",
                table: "ChatWorkspaces");

            migrationBuilder.DropColumn(
                name: "SandboxId",
                table: "ChatWorkspaces");

            migrationBuilder.DropColumn(
                name: "DynamicSessionId",
                table: "ChatConversations");

            migrationBuilder.DropColumn(
                name: "SandboxId",
                table: "ChatConversations");
        }
    }
}
