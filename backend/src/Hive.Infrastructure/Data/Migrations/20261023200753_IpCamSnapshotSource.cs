using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IpCamSnapshotSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rtsp_port",
                table: "nvrs",
                type: "integer",
                nullable: false,
                defaultValue: 554);

            migrationBuilder.AddColumn<string>(
                name: "snapshot_source",
                table: "nvr_channels",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "snapshot_via",
                table: "nvr_channels",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rtsp_port",
                table: "nvrs");

            migrationBuilder.DropColumn(
                name: "snapshot_source",
                table: "nvr_channels");

            migrationBuilder.DropColumn(
                name: "snapshot_via",
                table: "nvr_channels");
        }
    }
}
