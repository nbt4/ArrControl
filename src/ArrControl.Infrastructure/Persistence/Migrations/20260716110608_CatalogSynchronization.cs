using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogSynchronization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope_key",
                table: "schedules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_items",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parent_provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider_data_json = table.Column<string>(type: "jsonb", nullable: false),
                    fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_items", x => new { x.instance_id, x.provider_key });
                    table.ForeignKey(
                        name: "fk_provider_items_service_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "service_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    canonical_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: true),
                    season_number = table.Column<int>(type: "integer", nullable: true),
                    episode_number = table.Column<int>(type: "integer", nullable: true),
                    monitored = table.Column<bool>(type: "boolean", nullable: false),
                    has_file = table.Column<bool>(type: "boolean", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_ids_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_entities", x => x.id);
                    table.CheckConstraint("ck_media_entities_episode_number", "episode_number IS NULL OR episode_number >= 0");
                    table.CheckConstraint("ck_media_entities_season_number", "season_number IS NULL OR season_number >= 0");
                    table.CheckConstraint("ck_media_entities_year", "year IS NULL OR year > 0");
                    table.ForeignKey(
                        name: "fk_media_entities_provider_items_instance_provider_key",
                        columns: x => new { x.instance_id, x.provider_key },
                        principalTable: "provider_items",
                        principalColumns: new[] { "instance_id", "provider_key" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_schedules_type_scope_key",
                table: "schedules",
                columns: new[] { "type", "scope_key" },
                unique: true,
                filter: "scope_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_media_entities_missing_projection",
                table: "media_entities",
                columns: new[] { "instance_id", "canonical_kind", "monitored", "has_file", "available_at" });

            migrationBuilder.CreateIndex(
                name: "ux_media_entities_instance_id_provider_key",
                table: "media_entities",
                columns: new[] { "instance_id", "provider_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_items_instance_id_raw_kind",
                table: "provider_items",
                columns: new[] { "instance_id", "raw_kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_entities");

            migrationBuilder.DropTable(
                name: "provider_items");

            migrationBuilder.DropIndex(
                name: "ux_schedules_type_scope_key",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "scope_key",
                table: "schedules");
        }
    }
}
