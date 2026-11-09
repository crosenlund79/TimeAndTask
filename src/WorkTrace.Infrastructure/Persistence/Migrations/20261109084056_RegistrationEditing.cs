using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegistrationEditing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "ManualAllocations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualAllocations_ClientId",
                table: "ManualAllocations",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_ManualAllocations_Clients_ClientId",
                table: "ManualAllocations",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ManualAllocations_Clients_ClientId",
                table: "ManualAllocations");

            migrationBuilder.DropIndex(
                name: "IX_ManualAllocations_ClientId",
                table: "ManualAllocations");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "ManualAllocations");
        }
    }
}
