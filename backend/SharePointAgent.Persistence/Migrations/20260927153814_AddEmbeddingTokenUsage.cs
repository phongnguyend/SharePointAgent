using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmbeddingTokenUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EmbeddingModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeploymentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DriveId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FileId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChunkNumber = table.Column<int>(type: "int", nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingTokenUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingTokenUsage_AttachmentId",
                table: "EmbeddingTokenUsage",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingTokenUsage_DriveId_FileId",
                table: "EmbeddingTokenUsage",
                columns: new[] { "DriveId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingTokenUsage_EmbeddingModelId_CreatedAtUtc",
                table: "EmbeddingTokenUsage",
                columns: new[] { "EmbeddingModelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingTokenUsage_Operation_CreatedAtUtc",
                table: "EmbeddingTokenUsage",
                columns: new[] { "Operation", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingTokenUsage_QuestionId",
                table: "EmbeddingTokenUsage",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingTokenUsage_UserId_CreatedAtUtc",
                table: "EmbeddingTokenUsage",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmbeddingTokenUsage");
        }
    }
}
