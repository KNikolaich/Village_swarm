using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commands",
                columns: table => new
                {
                    cid = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    issued_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ttl_s = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ack_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ack_payload = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commands", x => x.cid);
                });

            migrationBuilder.CreateTable(
                name: "modes",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modes", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    channel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    event_id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    device_ids = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    silent = table.Column<bool>(type: "boolean", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    repeat_count = table.Column<int>(type: "integer", nullable: false),
                    edit_pending = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    wait_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expected_photos = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    reply_to_message_id = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tg_link_codes",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tg_link_codes", x => x.code);
                    table.ForeignKey(
                        name: "fk_tg_link_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tg_links",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notify_level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tg_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_tg_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "modes",
                columns: new[] { "key", "updated_at", "updated_by", "value" },
                values: new object[] { "armed", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "false" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_ts",
                table: "audit_log",
                column: "ts");

            migrationBuilder.CreateIndex(
                name: "ix_commands_device_id_issued_at",
                table: "commands",
                columns: new[] { "device_id", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_commands_status",
                table: "commands",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_event_id",
                table: "notifications",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_status_next_attempt_at",
                table: "notifications",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_target_dedupe_key_created_at",
                table: "notifications",
                columns: new[] { "target", "dedupe_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tg_link_codes_user_id",
                table: "tg_link_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tg_links_chat_id",
                table: "tg_links",
                column: "chat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tg_links_user_id",
                table: "tg_links",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "commands");

            migrationBuilder.DropTable(
                name: "modes");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "tg_link_codes");

            migrationBuilder.DropTable(
                name: "tg_links");
        }
    }
}
