using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baratrum.Migrations
{
    /// <inheritdoc />
    public partial class memory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LongTermMemories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MemoryGenerationDate = table.Column<long>(type: "bigint", nullable: false),
                    Memory = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LongTermMemories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LongTermMemories_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShortTermMemories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MemoryGenerationDate = table.Column<long>(type: "bigint", nullable: false),
                    Memory = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsConsumed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortTermMemories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShortTermMemories_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LongTermMemories_UserId",
                table: "LongTermMemories",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Active_Context",
                table: "ShortTermMemories",
                columns: new[] { "UserId", "MemoryGenerationDate" },
                filter: "[IsConsumed] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LongTermMemories");

            migrationBuilder.DropTable(
                name: "ShortTermMemories");
        }
    }
}
