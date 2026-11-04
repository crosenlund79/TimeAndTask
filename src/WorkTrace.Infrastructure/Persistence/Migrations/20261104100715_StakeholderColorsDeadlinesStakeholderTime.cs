using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StakeholderColorsDeadlinesStakeholderTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimesheetAdjustments_LocalDate_TaskId",
                table: "TimesheetAdjustments");

            migrationBuilder.AlterColumn<int>(
                name: "TaskId",
                table: "TimesheetAdjustments",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "TimesheetAdjustments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Deadline",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "Observations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Clients",
                type: "TEXT",
                maxLength: 9,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetAdjustments_ClientId",
                table: "TimesheetAdjustments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetAdjustments_LocalDate_TaskId_ClientId",
                table: "TimesheetAdjustments",
                columns: new[] { "LocalDate", "TaskId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ClientId",
                table: "Observations",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_Observations_Clients_ClientId",
                table: "Observations",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TimesheetAdjustments_Clients_ClientId",
                table: "TimesheetAdjustments",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Observations_Clients_ClientId",
                table: "Observations");

            migrationBuilder.DropForeignKey(
                name: "FK_TimesheetAdjustments_Clients_ClientId",
                table: "TimesheetAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_TimesheetAdjustments_ClientId",
                table: "TimesheetAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_TimesheetAdjustments_LocalDate_TaskId_ClientId",
                table: "TimesheetAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_Observations_ClientId",
                table: "Observations");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "TimesheetAdjustments");

            migrationBuilder.DropColumn(
                name: "Deadline",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Observations");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "Clients");

            migrationBuilder.AlterColumn<int>(
                name: "TaskId",
                table: "TimesheetAdjustments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetAdjustments_LocalDate_TaskId",
                table: "TimesheetAdjustments",
                columns: new[] { "LocalDate", "TaskId" },
                unique: true);
        }
    }
}
