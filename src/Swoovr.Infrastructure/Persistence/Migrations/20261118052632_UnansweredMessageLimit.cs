using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnansweredMessageLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "unanswered_limit",
                table: "notification_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_settings_unanswered_limit",
                table: "notification_settings",
                sql: "unanswered_limit IS NULL OR unanswered_limit IN (3, 5, 10)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_settings_unanswered_limit",
                table: "notification_settings");

            migrationBuilder.DropColumn(
                name: "unanswered_limit",
                table: "notification_settings");
        }
    }
}
