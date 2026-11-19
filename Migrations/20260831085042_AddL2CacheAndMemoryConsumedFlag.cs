using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baratrum.Migrations
{
    /// <inheritdoc />
    public partial class AddL2CacheAndMemoryConsumedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "L2ID",
                table: "LongTermMemories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MemoryConsumed",
                table: "LongTermMemories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "L2Cache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MemoryGenerationDate = table.Column<long>(type: "bigint", nullable: false),
                    Memory = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MemoryConsumed = table.Column<bool>(type: "bit", nullable: false),
                    L3ID = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_L2Cache", x => x.Id);
                    table.ForeignKey(
                        name: "FK_L2Cache_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_L2Cache_UserId",
                table: "L2Cache",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "L2Cache");

            migrationBuilder.DropColumn(
                name: "L2ID",
                table: "LongTermMemories");

            migrationBuilder.DropColumn(
                name: "MemoryConsumed",
                table: "LongTermMemories");
        }
    }
}
