using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceTurnUsageWithChatTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scaffolded with the drop first; reordered so the existing per-turn rows are carried across
            // before the table goes. Quotas read the new table from the moment this runs, so skipping
            // the copy would reset every user's month-to-date usage to zero.
            migrationBuilder.CreateTable(
                name: "ChatTokenUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ToolNames = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SkillNames = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ScriptPaths = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatTokenUsage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatTokenUsage_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatTokenUsage_ConversationId",
                table: "ChatTokenUsage",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTokenUsage_ModelId_Month",
                table: "ChatTokenUsage",
                columns: new[] { "ModelId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatTokenUsage_QuestionId_Sequence",
                table: "ChatTokenUsage",
                columns: new[] { "QuestionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatTokenUsage_UserId_CreatedAtUtc",
                table: "ChatTokenUsage",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatTokenUsage_UserId_Month",
                table: "ChatTokenUsage",
                columns: new[] { "UserId", "Month" });

            // Each historical turn becomes one whole-turn row (Sequence -1): the per-request breakdown
            // was never recorded for them and cannot be reconstructed. Day and Month carry over as they
            // are, so month-to-date quotas are unchanged by this migration. Id is left to the column
            // default. A turn whose message has since been deleted keeps its usage with an empty
            // conversation ID, because billed usage must outlive the conversation.
            migrationBuilder.Sql("""
                INSERT INTO [ChatTokenUsage]
                    ([CreatedAtUtc], [Day], [Month], [UserId], [ConversationId], [QuestionId], [Sequence],
                     [ModelId], [InputTokens], [OutputTokens], [TotalTokens])
                SELECT
                    ISNULL([u].[CreatedAtUtc], TODATETIMEOFFSET(SYSUTCDATETIME(), 0)),
                    [u].[Day],
                    [u].[Month],
                    [u].[UserId],
                    ISNULL([m].[ConversationId], CONVERT(uniqueidentifier, 0x0)),
                    [u].[QuestionId],
                    -1,
                    [u].[ModelId],
                    [u].[InputTokens],
                    [u].[OutputTokens],
                    [u].[TotalTokens]
                FROM [UserTokenUsage] AS [u]
                LEFT JOIN [ChatMessages] AS [m] ON [m].[Id] = [u].[QuestionId];
                """);

            migrationBuilder.DropTable(
                name: "UserTokenUsage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Also reordered, so going back collapses each turn's requests into the per-turn row it
            // replaced instead of discarding the ledger. The per-request detail is lost, which is
            // inherent to the older shape.
            migrationBuilder.CreateTable(
                name: "UserTokenUsage",
                columns: table => new
                {
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Day = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Month = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTokenUsage", x => x.QuestionId);
                    table.ForeignKey(
                        name: "FK_UserTokenUsage_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserTokenUsage_ModelId_Month",
                table: "UserTokenUsage",
                columns: new[] { "ModelId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_UserTokenUsage_UserId_Month",
                table: "UserTokenUsage",
                columns: new[] { "UserId", "Month" });

            // A row with no user cannot go back: the column it returns to is not nullable.
            migrationBuilder.Sql("""
                INSERT INTO [UserTokenUsage]
                    ([QuestionId], [CreatedAtUtc], [Day], [Month], [UserId], [ModelId],
                     [InputTokens], [OutputTokens], [TotalTokens])
                SELECT
                    [QuestionId],
                    MIN([CreatedAtUtc]),
                    MIN([Day]),
                    MIN([Month]),
                    MIN([UserId]),
                    MAX([ModelId]),
                    SUM(ISNULL([InputTokens], 0)),
                    SUM(ISNULL([OutputTokens], 0)),
                    SUM(ISNULL([TotalTokens], 0))
                FROM [ChatTokenUsage]
                WHERE [UserId] IS NOT NULL
                GROUP BY [QuestionId];
                """);

            migrationBuilder.DropTable(
                name: "ChatTokenUsage");
        }
    }
}
