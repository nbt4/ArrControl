using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_key = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    remediation_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snoozed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    snoozed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_incidents", x => x.id);
                    table.CheckConstraint("ck_health_incidents_resolution_order", "resolved_at IS NULL OR resolved_at >= first_seen_at");
                    table.CheckConstraint("ck_health_incidents_seen_order", "last_seen_at >= first_seen_at");
                    table.CheckConstraint("ck_health_incidents_severity", "severity IN ('ok','notice','warning','error','unknown')");
                    table.ForeignKey(
                        name: "fk_health_incidents_service_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "service_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_health_incidents_users_acknowledged_by_user_id",
                        column: x => x.acknowledged_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_health_incidents_users_snoozed_by_user_id",
                        column: x => x.snoozed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "health_incident_sources",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    provider_issue_id = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    remediation_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_incident_sources", x => new { x.incident_id, x.source_key });
                    table.CheckConstraint("ck_health_incident_sources_seen_order", "last_seen_at >= first_seen_at");
                    table.CheckConstraint("ck_health_incident_sources_severity", "severity IN ('ok','notice','warning','error','unknown')");
                    table.ForeignKey(
                        name: "fk_health_incident_sources_health_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "health_incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_incident_sources_incident_id_active",
                table: "health_incident_sources",
                columns: new[] { "incident_id", "active" });

            migrationBuilder.CreateIndex(
                name: "ix_health_incidents_acknowledged_by_user_id",
                table: "health_incidents",
                column: "acknowledged_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_incidents_instance_state_seen",
                table: "health_incidents",
                columns: new[] { "instance_id", "resolved_at", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ix_health_incidents_snoozed_by_user_id",
                table: "health_incidents",
                column: "snoozed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_health_incidents_instance_id_group_key",
                table: "health_incidents",
                columns: new[] { "instance_id", "group_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_incident_sources");

            migrationBuilder.DropTable(
                name: "health_incidents");
        }
    }
}
