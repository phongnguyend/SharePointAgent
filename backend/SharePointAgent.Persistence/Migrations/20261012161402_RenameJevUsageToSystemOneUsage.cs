using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations;

public partial class RenameJevUsageToSystemOneUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(name: "PK_JevTokenUsage", table: "JevTokenUsage");
        migrationBuilder.RenameTable(name: "JevTokenUsage", newName: "SystemOneUsage");
        migrationBuilder.RenameIndex(name: "IX_JevTokenUsage_Day", table: "SystemOneUsage", newName: "IX_SystemOneUsage_Day");
        migrationBuilder.RenameIndex(name: "IX_JevTokenUsage_ModelId_Month", table: "SystemOneUsage", newName: "IX_SystemOneUsage_ModelId_Month");
        migrationBuilder.RenameIndex(name: "IX_JevTokenUsage_TraceId", table: "SystemOneUsage", newName: "IX_SystemOneUsage_TraceId");
        migrationBuilder.RenameIndex(name: "IX_JevTokenUsage_UserId_CreatedAtUtc", table: "SystemOneUsage", newName: "IX_SystemOneUsage_UserId_CreatedAtUtc");
        migrationBuilder.AddPrimaryKey(name: "PK_SystemOneUsage", table: "SystemOneUsage", column: "Id");
        migrationBuilder.AddColumn<string>(name: "Provider", table: "SystemOneUsage", type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "Jev");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Provider", table: "SystemOneUsage");
        migrationBuilder.DropPrimaryKey(name: "PK_SystemOneUsage", table: "SystemOneUsage");
        migrationBuilder.RenameTable(name: "SystemOneUsage", newName: "JevTokenUsage");
        migrationBuilder.RenameIndex(name: "IX_SystemOneUsage_Day", table: "JevTokenUsage", newName: "IX_JevTokenUsage_Day");
        migrationBuilder.RenameIndex(name: "IX_SystemOneUsage_ModelId_Month", table: "JevTokenUsage", newName: "IX_JevTokenUsage_ModelId_Month");
        migrationBuilder.RenameIndex(name: "IX_SystemOneUsage_TraceId", table: "JevTokenUsage", newName: "IX_JevTokenUsage_TraceId");
        migrationBuilder.RenameIndex(name: "IX_SystemOneUsage_UserId_CreatedAtUtc", table: "JevTokenUsage", newName: "IX_JevTokenUsage_UserId_CreatedAtUtc");
        migrationBuilder.AddPrimaryKey(name: "PK_JevTokenUsage", table: "JevTokenUsage", column: "Id");
    }

}
