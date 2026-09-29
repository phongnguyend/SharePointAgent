using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "ChatConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FoundryEndpoint = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    FoundrySessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatWorkspaces_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversations_WorkspaceId",
                table: "ChatConversations",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatWorkspaces_CreatedById",
                table: "ChatWorkspaces",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ChatWorkspaces_UpdatedAtUtc",
                table: "ChatWorkspaces",
                column: "UpdatedAtUtc",
                descending: new bool[0]);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatConversations_ChatWorkspaces_WorkspaceId",
                table: "ChatConversations",
                column: "WorkspaceId",
                principalTable: "ChatWorkspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatConversations_ChatWorkspaces_WorkspaceId",
                table: "ChatConversations");

            migrationBuilder.DropTable(
                name: "ChatWorkspaces");

            migrationBuilder.DropIndex(
                name: "IX_ChatConversations_WorkspaceId",
                table: "ChatConversations");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ChatConversations");
        }
    }
}
