using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChooseWorkspaceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkspaceMode",
                table: "ChatWorkspaces",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceMode",
                table: "ChatConversations",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkspaceMode",
                table: "ChatWorkspaces");

            migrationBuilder.DropColumn(
                name: "WorkspaceMode",
                table: "ChatConversations");
        }
    }
}
