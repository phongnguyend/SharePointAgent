using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptionTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TranscriptionTokenUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AudioBytes = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<double>(type: "float", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscriptionTokenUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptionTokenUsage_ModelId_CreatedAtUtc",
                table: "TranscriptionTokenUsage",
                columns: new[] { "ModelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptionTokenUsage_UserId_Month",
                table: "TranscriptionTokenUsage",
                columns: new[] { "UserId", "Month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TranscriptionTokenUsage");
        }
    }
}
