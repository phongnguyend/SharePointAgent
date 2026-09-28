using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImageDescriptionUsagePeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Day",
                table: "ImageDescriptionTokenUsage",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Month",
                table: "ImageDescriptionTokenUsage",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE [ImageDescriptionTokenUsage]
                SET [Day] = CONVERT(int, CONVERT(char(8), SWITCHOFFSET([CreatedAtUtc], '+00:00'), 112)),
                    [Month] = CONVERT(int, CONVERT(char(6), SWITCHOFFSET([CreatedAtUtc], '+00:00'), 112));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_ModelId_Month",
                table: "ImageDescriptionTokenUsage",
                columns: new[] { "ModelId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_UserId_Month",
                table: "ImageDescriptionTokenUsage",
                columns: new[] { "UserId", "Month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageDescriptionTokenUsage_ModelId_Month",
                table: "ImageDescriptionTokenUsage");

            migrationBuilder.DropIndex(
                name: "IX_ImageDescriptionTokenUsage_UserId_Month",
                table: "ImageDescriptionTokenUsage");

            migrationBuilder.DropColumn(
                name: "Day",
                table: "ImageDescriptionTokenUsage");

            migrationBuilder.DropColumn(
                name: "Month",
                table: "ImageDescriptionTokenUsage");
        }
    }
}
