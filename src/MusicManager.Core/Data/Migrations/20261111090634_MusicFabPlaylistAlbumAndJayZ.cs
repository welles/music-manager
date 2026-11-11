using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicFabPlaylistAlbumAndJayZ : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DropPlaylistAlbum",
                table: "SourceProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Existing installations: the MusicFab profile drops playlist albums, and MusicBrainz's
            // stylised "JAŸ-Z" is written as "JAY-Z" (user decision 2026-11-11).
            migrationBuilder.Sql("UPDATE SourceProfiles SET DropPlaylistAlbum = 1 WHERE Name = 'MusicFab';");
            migrationBuilder.Sql("""
                INSERT INTO ArtistRules (Kind, Pattern, Replacement, Comment)
                SELECT 'ForceReplace', 'JAŸ-Z', 'JAY-Z', 'MusicBrainz schreibt den Namen stilisiert'
                WHERE NOT EXISTS (SELECT 1 FROM ArtistRules WHERE Pattern = 'JAŸ-Z');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DropPlaylistAlbum",
                table: "SourceProfiles");
        }
    }
}
