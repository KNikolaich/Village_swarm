﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InfraredTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "INSERT INTO device_types (code, caps, default_config, offline_after_s, title) VALUES ('irrx', NULL, NULL, 180, 'ИК-приёмник (учит кнопки пульта)') ON CONFLICT (code) DO UPDATE SET title = EXCLUDED.title WHERE device_types.title = device_types.code");
            migrationBuilder.Sql(
                "INSERT INTO device_types (code, caps, default_config, offline_after_s, title) VALUES ('irtx', NULL, NULL, 180, 'ИК-передатчик (управляет прибором)') ON CONFLICT (code) DO UPDATE SET title = EXCLUDED.title WHERE device_types.title = device_types.code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "device_types",
                keyColumn: "code",
                keyValue: "irrx");

            migrationBuilder.DeleteData(
                table: "device_types",
                keyColumn: "code",
                keyValue: "irtx");
        }
    }
}
