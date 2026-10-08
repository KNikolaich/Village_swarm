using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_health",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uptime_s = table.Column<long>(type: "bigint", nullable: false),
                    rssi = table.Column<int>(type: "integer", nullable: false),
                    heap_free = table.Column<long>(type: "bigint", nullable: false),
                    vbat = table.Column<double>(type: "double precision", nullable: true),
                    reset_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    clock_skew = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_health", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    msg = table.Column<string>(type: "text", nullable: false),
                    ctx = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_types",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    caps = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    default_config = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    offline_after_s = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_types", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    armed = table.Column<bool>(type: "boolean", nullable: true),
                    acknowledged_by = table.Column<string>(type: "text", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    via_node = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zones", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    zone_id = table.Column<int>(type: "integer", nullable: true),
                    mac = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    hw = table.Column<string>(type: "text", nullable: true),
                    fw_version = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ip = table.Column<string>(type: "text", nullable: true),
                    rssi = table.Column<int>(type: "integer", nullable: true),
                    boot = table.Column<int>(type: "integer", nullable: true),
                    info = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    state = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    config = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    config_rev = table.Column<int>(type: "integer", nullable: false),
                    config_applied_rev = table.Column<int>(type: "integer", nullable: true),
                    mqtt_user = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_device_types_type_code",
                        column: x => x.type_code,
                        principalTable: "device_types",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_devices_zones_zone_id",
                        column: x => x.zone_id,
                        principalTable: "zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "device_types",
                columns: new[] { "code", "caps", "default_config", "offline_after_s", "title" },
                values: new object[,]
                {
                    { "guard-cam", null, null, 180, "Камера с датчиком движения" },
                    { "heat", null, null, 180, "Обогрев" },
                    { "leak", null, null, 180, "Датчик протечки с краном" },
                    { "meteo", null, null, 180, "Метеодатчик" },
                    { "plant", null, null, 600, "Растения" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_health_device_id_ts",
                table: "device_health",
                columns: new[] { "device_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_device_logs_device_id_ts",
                table: "device_logs",
                columns: new[] { "device_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_device_id",
                table: "devices",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_type_code",
                table: "devices",
                column: "type_code");

            migrationBuilder.CreateIndex(
                name: "ix_devices_zone_id",
                table: "devices",
                column: "zone_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_device_id_ts",
                table: "events",
                columns: new[] { "device_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_events_ts",
                table: "events",
                column: "ts");

            // Monthly-partitioned table, not modelled by EF migrations.
            migrationBuilder.Sql(TelemetryPartitions.CreateTableSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TelemetryPartitions.DropTableSql);

            migrationBuilder.DropTable(
                name: "device_health");

            migrationBuilder.DropTable(
                name: "device_logs");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "device_types");

            migrationBuilder.DropTable(
                name: "zones");
        }
    }
}
