using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OmegleCloneMVC.Migrations
{
    /// <inheritdoc />
    public partial class PremiumAdded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPremium",
                table: "User",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PremiumUntil",
                table: "User",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPremium",
                table: "User");

            migrationBuilder.DropColumn(
                name: "PremiumUntil",
                table: "User");
        }
    }
}
