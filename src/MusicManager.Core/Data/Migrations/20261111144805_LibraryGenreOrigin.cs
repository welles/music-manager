using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryGenreOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenreOrigin",
                table: "Library",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenreOrigin",
                table: "Library");
        }
    }
}
