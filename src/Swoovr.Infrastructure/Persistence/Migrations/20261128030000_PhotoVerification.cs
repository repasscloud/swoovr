using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhotoVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "photo_verified_until",
                table: "member_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "photo_verification_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    written_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    profile_photos = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    evidence_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photo_verification_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_photo_verification_attempts_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "photo_verification_matches",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photo_verification_matches", x => new { x.account_id, x.media_id });
                    table.ForeignKey(
                        name: "fk_photo_verification_matches_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_photo_verification_attempts_account_id_issued_at",
                table: "photo_verification_attempts",
                columns: new[] { "account_id", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_photo_verification_attempts_media_id",
                table: "photo_verification_attempts",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_photo_verification_attempts_one_open",
                table: "photo_verification_attempts",
                column: "account_id",
                unique: true,
                filter: "state IN ('Issued', 'Submitted')");

            migrationBuilder.CreateIndex(
                name: "ix_photo_verification_matches_media_id",
                table: "photo_verification_matches",
                column: "media_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photo_verification_attempts");

            migrationBuilder.DropTable(
                name: "photo_verification_matches");

            migrationBuilder.DropColumn(
                name: "photo_verified_until",
                table: "member_profiles");
        }
    }
}
