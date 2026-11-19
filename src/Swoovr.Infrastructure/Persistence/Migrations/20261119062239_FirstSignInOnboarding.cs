using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Swoovr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FirstSignInOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "onboarding_completed_at",
                table: "member_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "onboarding_share_about",
                table: "member_profiles",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "onboarding_steps",
                table: "member_profiles",
                type: "text[]",
                nullable: false,
                // A database default so hosts still running older code can keep inserting profiles.
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "picture_text",
                table: "member_profiles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "picture_text_revision",
                table: "member_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "pronouns",
                table: "member_identities",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pronoun_id",
                table: "member_identities",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_pronouns",
                table: "member_identities",
                type: "boolean",
                nullable: false,
                // D382 (Q181.6 A): on by default, also for rows older hosts insert.
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "another", "gender" },
                column: "sort_order",
                value: 6);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "non-binary", "gender" },
                column: "sort_order",
                value: 5);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "unsaid", "gender" },
                column: "sort_order",
                value: 7);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "straight", "orientation" },
                column: "label",
                value: "Straight (heterosexual)");

            migrationBuilder.InsertData(
                table: "profile_options",
                columns: new[] { "id", "kind", "available", "label", "sort_order" },
                values: new object[,]
                {
                    { "trans-man", "gender", true, "Trans man", 3 },
                    { "trans-woman", "gender", true, "Trans woman", 4 },
                    { "any", "pronoun", true, "Any pronouns", 6 },
                    { "he-him", "pronoun", true, "He/Him", 1 },
                    { "he-they", "pronoun", true, "He/They", 4 },
                    { "name", "pronoun", true, "Use my name", 7 },
                    { "self", "pronoun", true, "Self-describe", 8 },
                    { "she-her", "pronoun", true, "She/Her", 2 },
                    { "she-they", "pronoun", true, "She/They", 5 },
                    { "they-them", "pronoun", true, "They/Them", 3 },
                    { "unsaid", "pronoun", true, "Prefer not to say", 9 }
                });

            // D382 (Q181.6 A): until now pronouns showed under "Show my gender" (D175), so the new switch starts as that
            // switch was: nothing hidden becomes visible.
            migrationBuilder.Sql("UPDATE member_identities SET show_pronouns = show_gender;");
            // Existing free text maps to the matching option (ignoring case and spaces), else Self-describe with the words kept.
            migrationBuilder.Sql("""
                UPDATE member_identities i SET pronoun_id = o.id, pronouns = NULL
                FROM profile_options o
                WHERE o.kind = 'pronoun' AND o.id <> 'self' AND i.pronouns IS NOT NULL
                  AND lower(regexp_replace(i.pronouns, '\s', '', 'g')) = lower(regexp_replace(o.label, '\s', '', 'g'));
                UPDATE member_identities SET pronoun_id = 'self', pronouns = btrim(pronouns)
                WHERE pronouns IS NOT NULL AND btrim(pronouns) <> '' AND pronoun_id IS NULL;
                UPDATE member_identities SET pronouns = NULL WHERE pronoun_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to free text (40 characters) and the five genders the older code knows.
            migrationBuilder.Sql("""
                UPDATE member_identities i SET pronouns = o.label
                FROM profile_options o
                WHERE o.kind = 'pronoun' AND o.id = i.pronoun_id AND i.pronoun_id NOT IN ('self', 'unsaid');
                UPDATE member_identities SET pronouns = left(pronouns, 40) WHERE pronouns IS NOT NULL;
                UPDATE member_identities SET gender_id = 'man' WHERE gender_id = 'trans-man';
                UPDATE member_identities SET gender_id = 'woman' WHERE gender_id = 'trans-woman';
                """);

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "trans-man", "gender" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "trans-woman", "gender" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "any", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "he-him", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "he-they", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "name", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "self", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "she-her", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "she-they", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "they-them", "pronoun" });

            migrationBuilder.DeleteData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "unsaid", "pronoun" });

            migrationBuilder.DropColumn(
                name: "onboarding_completed_at",
                table: "member_profiles");

            migrationBuilder.DropColumn(
                name: "onboarding_share_about",
                table: "member_profiles");

            migrationBuilder.DropColumn(
                name: "onboarding_steps",
                table: "member_profiles");

            migrationBuilder.DropColumn(
                name: "picture_text",
                table: "member_profiles");

            migrationBuilder.DropColumn(
                name: "picture_text_revision",
                table: "member_profiles");

            migrationBuilder.DropColumn(
                name: "pronoun_id",
                table: "member_identities");

            migrationBuilder.DropColumn(
                name: "show_pronouns",
                table: "member_identities");

            migrationBuilder.AlterColumn<string>(
                name: "pronouns",
                table: "member_identities",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "another", "gender" },
                column: "sort_order",
                value: 4);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "non-binary", "gender" },
                column: "sort_order",
                value: 3);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "unsaid", "gender" },
                column: "sort_order",
                value: 5);

            migrationBuilder.UpdateData(
                table: "profile_options",
                keyColumns: new[] { "id", "kind" },
                keyValues: new object[] { "straight", "orientation" },
                column: "label",
                value: "Straight");
        }
    }
}
