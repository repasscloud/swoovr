using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DobReviewIdApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "id_approved_at",
                table: "dob_reviews",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dob_review_id_views",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dob_review_id_views", x => x.id);
                    table.ForeignKey(
                        name: "fk_dob_review_id_views_dob_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "dob_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dob_review_id_views_review_id_actor_account_id_at",
                table: "dob_review_id_views",
                columns: new[] { "review_id", "actor_account_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dob_review_id_views");

            migrationBuilder.DropColumn(
                name: "id_approved_at",
                table: "dob_reviews");
        }
    }
}
