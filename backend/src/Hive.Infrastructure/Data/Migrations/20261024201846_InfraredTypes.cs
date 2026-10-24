using Microsoft.EntityFrameworkCore.Migrations;

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
            migrationBuilder.InsertData(
                table: "device_types",
                columns: new[] { "code", "caps", "default_config", "offline_after_s", "title" },
                values: new object[,]
                {
                    { "irrx", null, null, 180, "ИК-приёмник (учит кнопки пульта)" },
                    { "irtx", null, null, 180, "ИК-передатчик (управляет прибором)" }
                });
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
