using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Playlists",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "Playlists");
        }
    }
}
