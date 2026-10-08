using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Provisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrollment_codes",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    device_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    zone_id = table.Column<int>(type: "integer", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_by_mac = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment_codes", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_codes_device_id",
                table: "enrollment_codes",
                column: "device_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enrollment_codes");
        }
    }
}
