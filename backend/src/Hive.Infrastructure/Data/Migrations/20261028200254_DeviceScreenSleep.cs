using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceScreenSleep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "screen_sleep",
                table: "devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "screen_sleep_sha",
                table: "devices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "screen_sleep",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_sleep_sha",
                table: "devices");
        }
    }
}
