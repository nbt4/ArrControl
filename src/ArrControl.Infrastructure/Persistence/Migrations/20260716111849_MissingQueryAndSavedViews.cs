using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MissingQueryAndSavedViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "missing_items",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    monitored = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_missing_items", x => new { x.instance_id, x.provider_key });
                    table.CheckConstraint("ck_missing_items_reason", "reason IN ('missing', 'not_available')");
                    table.ForeignKey(
                        name: "fk_missing_items_provider_items_instance_provider_key",
                        columns: x => new { x.instance_id, x.provider_key },
                        principalTable: "provider_items",
                        principalColumns: new[] { "instance_id", "provider_key" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "missing_saved_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    filter_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_missing_saved_views", x => x.id);
                    table.ForeignKey(
                        name: "fk_missing_saved_views_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO missing_items (
                    instance_id, provider_key, reason, monitored, first_seen_at, updated_at)
                SELECT
                    media.instance_id,
                    media.provider_key,
                    CASE
                        WHEN media.available_at IS NOT NULL AND media.available_at > now()
                        THEN 'not_available'
                        ELSE 'missing'
                    END,
                    TRUE,
                    provider.first_seen_at,
                    provider.updated_at
                FROM media_entities AS media
                JOIN provider_items AS provider
                  ON provider.instance_id = media.instance_id
                 AND provider.provider_key = media.provider_key
                WHERE media.canonical_kind IN ('movie', 'episode')
                  AND media.monitored = TRUE
                  AND media.has_file = FALSE;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_missing_items_reason_updated_at",
                table: "missing_items",
                columns: new[] { "reason", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_missing_saved_views_user_id_name",
                table: "missing_saved_views",
                columns: new[] { "user_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "missing_items");

            migrationBuilder.DropTable(
                name: "missing_saved_views");
        }
    }
}
