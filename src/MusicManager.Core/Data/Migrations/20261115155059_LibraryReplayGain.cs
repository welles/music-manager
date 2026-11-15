using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryReplayGain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ReplayGainTrackGain",
                table: "Library",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ReplayGainTrackPeak",
                table: "Library",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayGainTrackGain",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "ReplayGainTrackPeak",
                table: "Library");
        }
    }
}
