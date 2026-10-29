using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRecords_Scope_CreatedAtUtc",
                table: "BackupRecords",
                columns: new[] { "Scope", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupRecords");
        }
    }
}
