using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerHeartbeatEntity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncSucceededUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActiveFailure = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SubscriptionExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeatEntity", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeatEntity_LastHeartbeatUtc",
                table: "WorkerHeartbeatEntity",
                column: "LastHeartbeatUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerHeartbeatEntity");
        }
    }
}
