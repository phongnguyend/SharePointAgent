using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentSafetyUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentSafetyUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ApiVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CharacterCount = table.Column<int>(type: "int", nullable: false),
                    EstimatedTextRecords = table.Column<int>(type: "int", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    HateSeverity = table.Column<int>(type: "int", nullable: true),
                    SexualSeverity = table.Column<int>(type: "int", nullable: true),
                    ViolenceSeverity = table.Column<int>(type: "int", nullable: true),
                    SelfHarmSeverity = table.Column<int>(type: "int", nullable: true),
                    HateThreshold = table.Column<int>(type: "int", nullable: false),
                    SexualThreshold = table.Column<int>(type: "int", nullable: false),
                    ViolenceThreshold = table.Column<int>(type: "int", nullable: false),
                    SelfHarmThreshold = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentSafetyUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentSafetyUsage_AssessmentId",
                table: "ContentSafetyUsage",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentSafetyUsage_AttachmentId",
                table: "ContentSafetyUsage",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentSafetyUsage_QuestionId",
                table: "ContentSafetyUsage",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentSafetyUsage_Status_CreatedAtUtc",
                table: "ContentSafetyUsage",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentSafetyUsage_UserId_CreatedAtUtc",
                table: "ContentSafetyUsage",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentSafetyUsage");
        }
    }
}
