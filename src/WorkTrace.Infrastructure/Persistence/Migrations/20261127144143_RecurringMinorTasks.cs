using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecurringMinorTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveFromMinute",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 480); // existing minor tasks get the recurring defaults

            migrationBuilder.AddColumn<int>(
                name: "ActiveUntilMinute",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 960); // existing minor tasks get the recurring defaults

            migrationBuilder.AddColumn<bool>(
                name: "ReminderDisabled",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RepeatDailyMinute",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 600); // existing minor tasks get the recurring defaults

            migrationBuilder.AddColumn<int>(
                name: "RepeatDays",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 31); // existing minor tasks get the recurring defaults

            migrationBuilder.AddColumn<int>(
                name: "RepeatIntervalMinutes",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60); // existing minor tasks get the recurring defaults

            migrationBuilder.AddColumn<int>(
                name: "RepeatKind",
                table: "MinorTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveFromMinute",
                table: "MinorTasks");

            migrationBuilder.DropColumn(
                name: "ActiveUntilMinute",
                table: "MinorTasks");

            migrationBuilder.DropColumn(
                name: "ReminderDisabled",
                table: "MinorTasks");

            migrationBuilder.DropColumn(
                name: "RepeatDailyMinute",
                table: "MinorTasks");

            migrationBuilder.DropColumn(
                name: "RepeatDays",
                table: "MinorTasks");

            migrationBuilder.DropColumn(
                name: "RepeatIntervalMinutes",
                table: "MinorTasks");

            migrationBuilder.DropColumn(
                name: "RepeatKind",
                table: "MinorTasks");
        }
    }
}
