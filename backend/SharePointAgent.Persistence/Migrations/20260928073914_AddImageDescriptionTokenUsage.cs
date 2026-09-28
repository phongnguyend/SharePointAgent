using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImageDescriptionTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageDescriptionTokenUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageDescriptionTokenUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_AttachmentId",
                table: "ImageDescriptionTokenUsage",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_ConversationId",
                table: "ImageDescriptionTokenUsage",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_ModelId_CreatedAtUtc",
                table: "ImageDescriptionTokenUsage",
                columns: new[] { "ModelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_QuestionId",
                table: "ImageDescriptionTokenUsage",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptionTokenUsage_UserId_CreatedAtUtc",
                table: "ImageDescriptionTokenUsage",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageDescriptionTokenUsage");
        }
    }
}
