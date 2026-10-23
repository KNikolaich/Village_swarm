using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IpCams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nvrs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    password_protected = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nvrs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nvr_channels",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nvr_id = table.Column<int>(type: "integer", nullable: false),
                    channel = table.Column<int>(type: "integer", nullable: false),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    record_when_disarmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nvr_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_nvr_channels_nvrs_nvr_id",
                        column: x => x.nvr_id,
                        principalTable: "nvrs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "device_types",
                columns: new[] { "code", "caps", "default_config", "offline_after_s", "title" },
                values: new object[] { "ipcam", null, null, 180, "IP-камера (через видеорегистратор)" });

            migrationBuilder.CreateIndex(
                name: "ix_nvr_channels_device_id",
                table: "nvr_channels",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nvr_channels_nvr_id_channel",
                table: "nvr_channels",
                columns: new[] { "nvr_id", "channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nvr_channels");

            migrationBuilder.DropTable(
                name: "nvrs");

            migrationBuilder.DeleteData(
                table: "device_types",
                keyColumn: "code",
                keyValue: "ipcam");
        }
    }
}
