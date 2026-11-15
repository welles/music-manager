using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpotifyMissingPlaylist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MissingPlaylistEnabled",
                table: "SpotifyAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MissingPlaylistId",
                table: "SpotifyAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                table: "SpotifyAccounts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MissingPlaylistEnabled",
                table: "SpotifyAccounts");

            migrationBuilder.DropColumn(
                name: "MissingPlaylistId",
                table: "SpotifyAccounts");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "SpotifyAccounts");
        }
    }
}
