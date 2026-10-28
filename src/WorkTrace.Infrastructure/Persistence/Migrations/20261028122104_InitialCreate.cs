using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsUndo = table.Column<bool>(type: "INTEGER", nullable: false),
                    UndoesBatchId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsInternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommunicationChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunicationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LockPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LockPeriods", x => x.Id);
                    table.CheckConstraint("CK_LockPeriods_Range", "EndUtc IS NULL OR EndUtc >= StartUtc");
                });

            migrationBuilder.CreateTable(
                name: "Screenshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Screenshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskSystems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSystems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackingSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndReason = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingSegments", x => x.Id);
                    table.CheckConstraint("CK_TrackingSegments_Range", "EndUtc IS NULL OR EndUtc >= StartUtc");
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskSystemId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true, collation: "NOCASE"),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    DefaultChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tasks_CommunicationChannels_DefaultChannelId",
                        column: x => x.DefaultChannelId,
                        principalTable: "CommunicationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tasks_TaskSystems_TaskSystemId",
                        column: x => x.TaskSystemId,
                        principalTable: "TaskSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManualAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualAllocations", x => x.Id);
                    table.CheckConstraint("CK_ManualAllocations_Range", "EndUtc > StartUtc");
                    table.CheckConstraint("CK_ManualAllocations_Task", "(Kind = 0 AND TaskId IS NOT NULL) OR (Kind <> 0 AND TaskId IS NULL)");
                    table.ForeignKey(
                        name: "FK_ManualAllocations_CommunicationChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "CommunicationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualAllocations_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OriginalObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OriginalTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    OriginalNote = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Observations_CommunicationChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "CommunicationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Tasks_OriginalTaskId",
                        column: x => x.OriginalTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimesheetAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    AdjustedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    BaselineMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimesheetAdjustments", x => x.Id);
                    table.CheckConstraint("CK_TimesheetAdjustments_Minutes", "AdjustedMinutes >= 0 AND BaselineMinutes >= 0");
                    table.ForeignKey(
                        name: "FK_TimesheetAdjustments_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CheckInPrompts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScheduledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ShownAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservationId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckInPrompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckInPrompts_Observations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "Observations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_BatchId",
                table: "ChangeLog",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_ChangedAtUtc",
                table: "ChangeLog",
                column: "ChangedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_EntityType_EntityId",
                table: "ChangeLog",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckInPrompts_ObservationId",
                table: "CheckInPrompts",
                column: "ObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckInPrompts_ScheduledAtUtc",
                table: "CheckInPrompts",
                column: "ScheduledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationChannels_Name",
                table: "CommunicationChannels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LockPeriods_StartUtc",
                table: "LockPeriods",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ManualAllocations_ChannelId",
                table: "ManualAllocations",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualAllocations_StartUtc_EndUtc",
                table: "ManualAllocations",
                columns: new[] { "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualAllocations_TaskId",
                table: "ManualAllocations",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ChannelId",
                table: "Observations",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ObservedAtUtc",
                table: "Observations",
                column: "ObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_OriginalTaskId",
                table: "Observations",
                column: "OriginalTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_TaskId",
                table: "Observations",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_CapturedAtUtc",
                table: "Screenshots",
                column: "CapturedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_RelativePath",
                table: "Screenshots",
                column: "RelativePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ClientId_TaskSystemId_ExternalId",
                table: "Tasks",
                columns: new[] { "ClientId", "TaskSystemId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_DefaultChannelId",
                table: "Tasks",
                column: "DefaultChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_LastUsedAtUtc",
                table: "Tasks",
                column: "LastUsedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TaskSystemId",
                table: "Tasks",
                column: "TaskSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSystems_Name",
                table: "TaskSystems",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetAdjustments_LocalDate_TaskId",
                table: "TimesheetAdjustments",
                columns: new[] { "LocalDate", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetAdjustments_TaskId",
                table: "TimesheetAdjustments",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingSegments_StartUtc",
                table: "TrackingSegments",
                column: "StartUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeLog");

            migrationBuilder.DropTable(
                name: "CheckInPrompts");

            migrationBuilder.DropTable(
                name: "LockPeriods");

            migrationBuilder.DropTable(
                name: "ManualAllocations");

            migrationBuilder.DropTable(
                name: "Screenshots");

            migrationBuilder.DropTable(
                name: "TimesheetAdjustments");

            migrationBuilder.DropTable(
                name: "TrackingSegments");

            migrationBuilder.DropTable(
                name: "Observations");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "CommunicationChannels");

            migrationBuilder.DropTable(
                name: "TaskSystems");
        }
    }
}
