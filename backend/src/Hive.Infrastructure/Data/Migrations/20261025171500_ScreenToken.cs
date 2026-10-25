using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScreenToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "screen_read_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "screen_read_external",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "screen_read_from",
                table: "devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "screen_token_hash",
                table: "devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "screen_token_issued_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_screen_token_hash",
                table: "devices",
                column: "screen_token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_screen_token_hash",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_read_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_read_external",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_read_from",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_token_hash",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_token_issued_at",
                table: "devices");
        }
    }
}
