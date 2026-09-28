using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImageDescriptionText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ImageDescriptionTokenUsage",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "ImageDescriptionTokenUsage",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "ImageDescriptionTokenUsage",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "ImageDescriptionTokenUsage");

            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "ImageDescriptionTokenUsage");

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "ImageDescriptionTokenUsage");
        }
    }
}
