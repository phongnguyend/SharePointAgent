using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SharePointAgent.Persistence.Migrations;

[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260926120000_AddFileSensitivity")]
public sealed class AddFileSensitivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("SensitivityLabelId", "SharePointIndexedFiles", type: "nvarchar(36)", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<string>("SensitivityLabelName", "SharePointIndexedFiles", type: "nvarchar(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<bool>("IsLabeled", "SharePointIndexedFiles", type: "bit", nullable: true);
        migrationBuilder.AddColumn<bool>("IsEncrypted", "SharePointIndexedFiles", type: "bit", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("SensitivityCheckedAtUtc", "SharePointIndexedFiles", type: "datetimeoffset(7)", precision: 7, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var column in new[] { "SensitivityLabelId", "SensitivityLabelName", "IsLabeled", "IsEncrypted", "SensitivityCheckedAtUtc" })
            migrationBuilder.DropColumn(column, "SharePointIndexedFiles");
    }
}
