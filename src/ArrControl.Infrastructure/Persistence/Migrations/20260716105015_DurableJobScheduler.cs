using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableJobScheduler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_job_runs_claim",
                table: "job_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_runs_lease_pair",
                table: "job_runs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_enqueued_at",
                table: "schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "available_at",
                table: "job_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_heartbeat_at",
                table: "job_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "job_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE job_runs
                SET available_at = scheduled_for,
                    state = CASE
                        WHEN completed_at IS NOT NULL AND error_code IS NULL THEN 'succeeded'
                        WHEN completed_at IS NOT NULL THEN 'failed'
                        ELSE 'pending'
                    END,
                    lease_owner = NULL,
                    lease_until = NULL,
                    lease_token = NULL,
                    last_heartbeat_at = NULL;

                UPDATE schedules AS schedule
                SET last_enqueued_at = materialized.last_enqueued_at
                FROM
                (
                    SELECT schedule_id, max(scheduled_for) AS last_enqueued_at
                    FROM job_runs
                    GROUP BY schedule_id
                ) AS materialized
                WHERE schedule.id = materialized.schedule_id;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "available_at",
                table: "job_runs",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "sync_checkpoints",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cursor = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_checkpoints", x => new { x.instance_id, x.stream });
                    table.ForeignKey(
                        name: "fk_sync_checkpoints_service_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "service_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_runs_claim",
                table: "job_runs",
                columns: new[] { "state", "available_at", "lease_until" },
                filter: "completed_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_runs_available_order",
                table: "job_runs",
                sql: "available_at >= scheduled_for");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_runs_lease_pair",
                table: "job_runs",
                sql: "(lease_owner IS NULL AND lease_until IS NULL AND lease_token IS NULL AND last_heartbeat_at IS NULL) OR (lease_owner IS NOT NULL AND lease_until IS NOT NULL AND lease_token IS NOT NULL AND last_heartbeat_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_runs_state",
                table: "job_runs",
                sql: "state IN ('pending', 'running', 'retry', 'succeeded', 'failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_runs_state_completion",
                table: "job_runs",
                sql: "(state IN ('succeeded', 'failed')) = (completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_runs_state_lease",
                table: "job_runs",
                sql: "(state = 'running') = (lease_token IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_checkpoints");

            migrationBuilder.DropIndex(
                name: "ix_job_runs_claim",
                table: "job_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_runs_available_order",
                table: "job_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_runs_lease_pair",
                table: "job_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_runs_state",
                table: "job_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_runs_state_completion",
                table: "job_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_runs_state_lease",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "last_enqueued_at",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "available_at",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "last_heartbeat_at",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "job_runs");

            migrationBuilder.CreateIndex(
                name: "ix_job_runs_claim",
                table: "job_runs",
                columns: new[] { "state", "scheduled_for", "lease_until" },
                filter: "completed_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_runs_lease_pair",
                table: "job_runs",
                sql: "(lease_owner IS NULL AND lease_until IS NULL) OR (lease_owner IS NOT NULL AND lease_until IS NOT NULL)");
        }
    }
}
