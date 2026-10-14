using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hive.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    definition = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    state = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rule_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rule_id = table.Column<int>(type: "integer", nullable: false),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_rule_runs_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rule_runs_rule_id_ts",
                table: "rule_runs",
                columns: new[] { "rule_id", "ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rule_runs");

            migrationBuilder.DropTable(
                name: "rules");
        }
    }
}
