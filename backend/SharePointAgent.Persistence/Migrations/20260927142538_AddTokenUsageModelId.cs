using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageModelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "UserTokenUsage",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTokenUsage_ModelId_Month",
                table: "UserTokenUsage",
                columns: new[] { "ModelId", "Month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserTokenUsage_ModelId_Month",
                table: "UserTokenUsage");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "UserTokenUsage");
        }
    }
}
