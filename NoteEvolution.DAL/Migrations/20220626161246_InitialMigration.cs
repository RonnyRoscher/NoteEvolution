using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoteEvolution.DAL.Migrations
{
    public partial class InitialMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModificationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Languages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModificationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    OrderNr = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Languages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TextUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModificationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RelatedDocumentId = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    SuccessorId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TextUnits_Documents_RelatedDocumentId",
                        column: x => x.RelatedDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TextUnits_TextUnits_ParentId",
                        column: x => x.ParentId,
                        principalTable: "TextUnits",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TextUnits_TextUnits_SuccessorId",
                        column: x => x.SuccessorId,
                        principalTable: "TextUnits",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LanguageId = table.Column<byte>(type: "INTEGER", nullable: false),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModificationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    RelatedTextUnitId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notes_TextUnits_RelatedTextUnitId",
                        column: x => x.RelatedTextUnitId,
                        principalTable: "TextUnits",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ContentSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModificationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Chapter = table.Column<string>(type: "TEXT", nullable: true),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RelatedNoteId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedTextUnitId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentSources_Notes_RelatedNoteId",
                        column: x => x.RelatedNoteId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentSources_TextUnits_RelatedTextUnitId",
                        column: x => x.RelatedTextUnitId,
                        principalTable: "TextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteNote",
                columns: table => new
                {
                    DerivedNotesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceNotesId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteNote", x => new { x.DerivedNotesId, x.SourceNotesId });
                    table.ForeignKey(
                        name: "FK_NoteNote_Notes_DerivedNotesId",
                        column: x => x.DerivedNotesId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteNote_Notes_SourceNotesId",
                        column: x => x.SourceNotesId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentSources_RelatedNoteId",
                table: "ContentSources",
                column: "RelatedNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentSources_RelatedTextUnitId",
                table: "ContentSources",
                column: "RelatedTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteNote_SourceNotesId",
                table: "NoteNote",
                column: "SourceNotesId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_RelatedTextUnitId",
                table: "Notes",
                column: "RelatedTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_TextUnits_ParentId",
                table: "TextUnits",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_TextUnits_RelatedDocumentId",
                table: "TextUnits",
                column: "RelatedDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_TextUnits_SuccessorId",
                table: "TextUnits",
                column: "SuccessorId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentSources");

            migrationBuilder.DropTable(
                name: "Languages");

            migrationBuilder.DropTable(
                name: "NoteNote");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropTable(
                name: "TextUnits");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}
