using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AggregatedActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "history_items",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    media_provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    download_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    correlation_key = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_history_items", x => new { x.instance_id, x.provider_key });
                    table.ForeignKey(
                        name: "fk_history_items_service_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "service_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "queue_items",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    media_provider_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    download_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    correlation_key = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tracked_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tracked_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    remaining_bytes = table.Column<long>(type: "bigint", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    estimated_completion_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    download_client = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    indexer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queue_items", x => new { x.instance_id, x.provider_key });
                    table.CheckConstraint("ck_queue_items_remaining", "remaining_bytes >= 0");
                    table.CheckConstraint("ck_queue_items_size", "size_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_queue_items_service_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "service_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_history_items_instance_id_correlation_key_event_at",
                table: "history_items",
                columns: new[] { "instance_id", "correlation_key", "event_at" });

            migrationBuilder.CreateIndex(
                name: "ix_history_items_instance_id_event_at",
                table: "history_items",
                columns: new[] { "instance_id", "event_at" });

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_instance_id_correlation_key",
                table: "queue_items",
                columns: new[] { "instance_id", "correlation_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "history_items");

            migrationBuilder.DropTable(
                name: "queue_items");
        }
    }
}
