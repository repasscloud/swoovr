using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_reads",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_reads", x => new { x.conversation_id, x.account_id });
                    table.ForeignKey(
                        name: "fk_conversation_reads_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_conversation_reads_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_reads_account_id",
                table: "conversation_reads",
                column: "account_id");

            // Issue 541 DEFAULT: conversations from before unread counts start read as of this migration, so members
            // don't see their whole history as unread after the deploy.
            migrationBuilder.Sql("""
                INSERT INTO conversation_reads (conversation_id, account_id, read_at)
                SELECT c.id, a.id, now() FROM conversations c JOIN accounts a ON a.id IN (c.pair_low, c.pair_high)
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_reads");
        }
    }
}
