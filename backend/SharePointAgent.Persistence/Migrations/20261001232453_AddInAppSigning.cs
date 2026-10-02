using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInAppSigning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "SignatureRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FieldsJson",
                table: "SignatureRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalSha256",
                table: "SignatureRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedDocumentBlobName",
                table: "SignatureRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedSha256",
                table: "SignatureRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "FieldsJson",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "OriginalSha256",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "SignedDocumentBlobName",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "SignedSha256",
                table: "SignatureRequests");
        }
    }
}
