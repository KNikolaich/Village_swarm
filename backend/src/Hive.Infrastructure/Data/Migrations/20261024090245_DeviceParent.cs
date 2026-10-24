using Microsoft.EntityFrameworkCore.Migrations;

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

            migrationBuilder.InsertData(
                table: "device_types",
                columns: new[] { "code", "caps", "default_config", "offline_after_s", "title" },
                values: new object[] { "nvr", null, null, 180, "Видеорегистратор" });

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
