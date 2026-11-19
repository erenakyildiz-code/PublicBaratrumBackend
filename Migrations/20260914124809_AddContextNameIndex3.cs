using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baratrum.Migrations
{
    /// <inheritdoc />
    public partial class AddContextNameIndex3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextConsumed",
                table: "AgenticContextRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContextConsumed",
                table: "AgenticContextRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
