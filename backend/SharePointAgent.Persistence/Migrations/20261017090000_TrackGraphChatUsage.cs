using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SharePointAgent.Persistence.Migrations;

[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20261017090000_TrackGraphChatUsage")]
public sealed class TrackGraphChatUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Operation", table: "ChatTokenUsage", type: "nvarchar(100)", maxLength: 100, nullable: true);
        migrationBuilder.AlterColumn<Guid>(name: "ConversationId", table: "ChatTokenUsage", type: "uniqueidentifier", nullable: true, oldClrType: typeof(Guid), oldType: "uniqueidentifier");
        migrationBuilder.AlterColumn<Guid>(name: "QuestionId", table: "ChatTokenUsage", type: "uniqueidentifier", nullable: true, oldClrType: typeof(Guid), oldType: "uniqueidentifier");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Operation", table: "ChatTokenUsage");
        migrationBuilder.AlterColumn<Guid>(name: "ConversationId", table: "ChatTokenUsage", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty, oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(name: "QuestionId", table: "ChatTokenUsage", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty, oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
    }
}
