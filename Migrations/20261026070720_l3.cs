using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baratrum.Migrations
{
    /// <inheritdoc />
    public partial class l3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "L3Cache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MemoryGenerationDate = table.Column<long>(type: "bigint", nullable: false),
                    Memory = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MemoryConsumed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_L3Cache", x => x.Id);
                    table.ForeignKey(
                        name: "FK_L3Cache_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_L3Cache_UserId",
                table: "L3Cache",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "L3Cache");
        }
    }
}
