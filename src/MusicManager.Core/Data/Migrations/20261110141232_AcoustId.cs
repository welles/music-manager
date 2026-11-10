using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AcoustId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcoustId",
                table: "Files",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcoustId",
                table: "Files");
        }
    }
}
