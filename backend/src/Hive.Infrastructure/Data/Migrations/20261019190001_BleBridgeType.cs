﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BleBridgeType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "INSERT INTO device_types (code, caps, default_config, offline_after_s, title) VALUES ('ble', NULL, NULL, 180, 'Мост Bluetooth (кухонный термометр)') ON CONFLICT (code) DO UPDATE SET title = EXCLUDED.title WHERE device_types.title = device_types.code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "device_types",
                keyColumn: "code",
                keyValue: "ble");
        }
    }
}
