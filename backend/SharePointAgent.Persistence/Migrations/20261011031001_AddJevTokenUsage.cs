using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJevTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JevTokenUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    QuestionCount = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JevTokenUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JevTokenUsage_Day",
                table: "JevTokenUsage",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_JevTokenUsage_ModelId_Month",
                table: "JevTokenUsage",
                columns: new[] { "ModelId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_JevTokenUsage_TraceId",
                table: "JevTokenUsage",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_JevTokenUsage_UserId_CreatedAtUtc",
                table: "JevTokenUsage",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JevTokenUsage");
        }
    }
}
