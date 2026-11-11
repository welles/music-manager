using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryCatalogFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlbumArtists",
                table: "Library",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Artists",
                table: "Library",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DiscNumber",
                table: "Library",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "Library",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HasCover",
                table: "Library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MbAlbumArtistIds",
                table: "Library",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MbArtistIds",
                table: "Library",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MbReleaseGroupId",
                table: "Library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RulesVersion",
                table: "Library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackNumber",
                table: "Library",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "Library",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlbumArtists",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "Artists",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "DiscNumber",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "HasCover",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "MbAlbumArtistIds",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "MbArtistIds",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "MbReleaseGroupId",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "RulesVersion",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "TrackNumber",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "Library");
        }
    }
}
