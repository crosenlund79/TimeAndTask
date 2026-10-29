using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkTrace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBoard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BoardCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Acceptance = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    WorkTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardCards_Tasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BoardState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    NextNumber = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BoardAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardAttachments_BoardCards_CardId",
                        column: x => x.CardId,
                        principalTable: "BoardCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BoardCardEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    From = table.Column<int>(type: "INTEGER", nullable: true),
                    To = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardCardEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardCardEvents_BoardCards_CardId",
                        column: x => x.CardId,
                        principalTable: "BoardCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BoardComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardComments_BoardCards_CardId",
                        column: x => x.CardId,
                        principalTable: "BoardCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BoardDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedByCardId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardDependencies", x => x.Id);
                    table.CheckConstraint("CK_BoardDependencies_NotSelf", "CardId <> BlockedByCardId");
                    table.ForeignKey(
                        name: "FK_BoardDependencies_BoardCards_BlockedByCardId",
                        column: x => x.BlockedByCardId,
                        principalTable: "BoardCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BoardDependencies_BoardCards_CardId",
                        column: x => x.CardId,
                        principalTable: "BoardCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BoardLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardLinks_BoardCards_CardId",
                        column: x => x.CardId,
                        principalTable: "BoardCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "BoardState",
                columns: new[] { "Id", "NextNumber" },
                values: new object[] { 1, 1 });

            migrationBuilder.CreateIndex(
                name: "IX_BoardAttachments_CardId",
                table: "BoardAttachments",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardAttachments_Uid",
                table: "BoardAttachments",
                column: "Uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardCardEvents_CardId",
                table: "BoardCardEvents",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardCardEvents_Uid",
                table: "BoardCardEvents",
                column: "Uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardCards_Number",
                table: "BoardCards",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardCards_Status_SortOrder",
                table: "BoardCards",
                columns: new[] { "Status", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_BoardCards_Uid",
                table: "BoardCards",
                column: "Uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardCards_WorkTaskId",
                table: "BoardCards",
                column: "WorkTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardComments_CardId",
                table: "BoardComments",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardComments_Uid",
                table: "BoardComments",
                column: "Uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardDependencies_BlockedByCardId",
                table: "BoardDependencies",
                column: "BlockedByCardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardDependencies_CardId_BlockedByCardId",
                table: "BoardDependencies",
                columns: new[] { "CardId", "BlockedByCardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardLinks_CardId",
                table: "BoardLinks",
                column: "CardId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardAttachments");

            migrationBuilder.DropTable(
                name: "BoardCardEvents");

            migrationBuilder.DropTable(
                name: "BoardComments");

            migrationBuilder.DropTable(
                name: "BoardDependencies");

            migrationBuilder.DropTable(
                name: "BoardLinks");

            migrationBuilder.DropTable(
                name: "BoardState");

            migrationBuilder.DropTable(
                name: "BoardCards");
        }
    }
}
