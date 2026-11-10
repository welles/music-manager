using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Library",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    BitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Artist = table.Column<string>(type: "TEXT", nullable: true),
                    Album = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtist = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<string>(type: "TEXT", nullable: true),
                    MbRecordingId = table.Column<string>(type: "TEXT", nullable: true),
                    MbReleaseId = table.Column<string>(type: "TEXT", nullable: true),
                    AcoustId = table.Column<string>(type: "TEXT", nullable: true),
                    TagSource = table.Column<string>(type: "TEXT", nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    FingerprintFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    IndexedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Library", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Library_DurationSeconds",
                table: "Library",
                column: "DurationSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_Library_MbRecordingId",
                table: "Library",
                column: "MbRecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Library_RelativePath",
                table: "Library",
                column: "RelativePath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Library");
        }
    }
}
