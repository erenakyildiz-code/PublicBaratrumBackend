using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baratrum.Migrations
{
    /// <inheritdoc />
    public partial class agency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Goal",
                table: "AgenticContextRecords",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Goal",
                table: "AgenticContextRecords");
        }
    }
}
