using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RelayType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "device_types",
                columns: new[] { "code", "caps", "default_config", "offline_after_s", "title" },
                values: new object[] { "relay", null, null, 180, "Реле (свет, розетка)" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "device_types",
                keyColumn: "code",
                keyValue: "relay");
        }
    }
}
