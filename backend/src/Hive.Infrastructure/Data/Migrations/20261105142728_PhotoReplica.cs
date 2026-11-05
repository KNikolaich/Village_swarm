using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoReplica : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "replica_next_at",
                table: "media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "replica_tries",
                table: "media",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "replicated_at",
                table: "media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_replicated_at",
                table: "media",
                column: "replicated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_media_replicated_at",
                table: "media");

            migrationBuilder.DropColumn(
                name: "replica_next_at",
                table: "media");

            migrationBuilder.DropColumn(
                name: "replica_tries",
                table: "media");

            migrationBuilder.DropColumn(
                name: "replicated_at",
                table: "media");
        }
    }
}
