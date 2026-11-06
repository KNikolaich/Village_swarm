using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DisplayCustom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "screen_background",
                table: "devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "screen_background_sha",
                table: "devices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "display_fonts",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bold = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cyrillic = table.Column<bool>(type: "boolean", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    digits_only = table.Column<bool>(type: "boolean", nullable: false),
                    family = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    family_id = table.Column<string>(type: "character varying(56)", maxLength: 56, nullable: false),
                    italic = table.Column<bool>(type: "boolean", nullable: false),
                    sha = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_display_fonts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_display_fonts_family_id",
                table: "display_fonts",
                column: "family_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "display_fonts");

            migrationBuilder.DropColumn(
                name: "screen_background",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "screen_background_sha",
                table: "devices");
        }
    }
}
