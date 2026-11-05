using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MinorTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MinorTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReminderAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SnoozedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinorTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinorTasks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinorTasks_ClientId",
                table: "MinorTasks",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_MinorTasks_IsCompleted_ReminderAtUtc",
                table: "MinorTasks",
                columns: new[] { "IsCompleted", "ReminderAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinorTasks");
        }
    }
}
