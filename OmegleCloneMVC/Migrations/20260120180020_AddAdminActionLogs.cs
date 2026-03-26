using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OmegleCloneMVC.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminActionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Mail",
                table: "User",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateTable(
                name: "AdminActionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    ActorEmail = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetUserId = table.Column<int>(type: "int", nullable: true),
                    TargetEmail = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminActionLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_User_Mail",
                table: "User",
                column: "Mail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminActionLogs_CreatedUtc",
                table: "AdminActionLogs",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminActionLogs");

            migrationBuilder.DropIndex(
                name: "IX_User_Mail",
                table: "User");

            migrationBuilder.AlterColumn<string>(
                name: "Mail",
                table: "User",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
