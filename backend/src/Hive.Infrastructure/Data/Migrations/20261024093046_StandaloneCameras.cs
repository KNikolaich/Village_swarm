using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class StandaloneCameras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "events_url",
                table: "nvrs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "nvrs",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "nvr");

            migrationBuilder.AddColumn<string>(
                name: "rtsp_main",
                table: "nvrs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rtsp_sub",
                table: "nvrs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "snapshot_url",
                table: "nvrs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vendor",
                table: "nvrs",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "dahua");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "events_url",
                table: "nvrs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "nvrs");

            migrationBuilder.DropColumn(
                name: "rtsp_main",
                table: "nvrs");

            migrationBuilder.DropColumn(
                name: "rtsp_sub",
                table: "nvrs");

            migrationBuilder.DropColumn(
                name: "snapshot_url",
                table: "nvrs");

            migrationBuilder.DropColumn(
                name: "vendor",
                table: "nvrs");
        }
    }
}
