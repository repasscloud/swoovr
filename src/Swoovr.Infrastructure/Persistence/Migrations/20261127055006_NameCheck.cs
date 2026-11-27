using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NameCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "name_change_reviews",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name_check",
                table: "member_profiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Issue 533: existing members have no open name check.
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "name_check_at",
                table: "member_profiles",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason",
                table: "name_change_reviews");

            migrationBuilder.DropColumn(
                name: "name_check",
                table: "member_profiles");

            migrationBuilder.DropColumn(
                name: "name_check_at",
                table: "member_profiles");
        }
    }
}
