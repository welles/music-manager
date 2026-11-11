using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubsonicCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AddedAt",
                table: "Library",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompilation",
                table: "Library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Isrc",
                table: "Library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MbTrackId",
                table: "Library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalDate",
                table: "Library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseTypes",
                table: "Library",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SongId",
                table: "Library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Library_SongId",
                table: "Library",
                column: "SongId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Library_SongId",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "AddedAt",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "IsCompilation",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "Isrc",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "MbTrackId",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "OriginalDate",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "ReleaseTypes",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "SongId",
                table: "Library");
        }
    }
}
