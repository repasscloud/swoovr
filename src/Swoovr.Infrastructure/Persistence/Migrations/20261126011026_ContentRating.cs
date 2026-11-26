using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_rating",
                table: "media_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Standard");

            // Issue 431 (D389): every photo a reviewer approved for albums only is explicit.
            migrationBuilder.Sql("UPDATE media_items SET content_rating = 'Explicit' WHERE album_only;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_rating",
                table: "media_items");
        }
    }
}
