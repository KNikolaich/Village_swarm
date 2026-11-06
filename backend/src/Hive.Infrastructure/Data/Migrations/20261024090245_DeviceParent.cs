﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "parent_id",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                "INSERT INTO device_types (code, caps, default_config, offline_after_s, title) VALUES ('nvr', NULL, NULL, 180, 'Видеорегистратор') ON CONFLICT (code) DO UPDATE SET title = EXCLUDED.title WHERE device_types.title = device_types.code");

            migrationBuilder.CreateIndex(
                name: "ix_devices_parent_id",
                table: "devices",
                column: "parent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_parent_id",
                table: "devices");

            migrationBuilder.DeleteData(
                table: "device_types",
                keyColumn: "code",
                keyValue: "nvr");

            migrationBuilder.DropColumn(
                name: "parent_id",
                table: "devices");
        }
    }
}
