using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_refresh_token_hash_length",
                table: "user_sessions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "access_expires_at",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "access_token_hash",
                table: "user_sessions",
                type: "bytea",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE user_sessions
                SET access_token_hash = decode(
                        replace(id::text, '-', '') || replace(id::text, '-', ''),
                        'hex'),
                    access_expires_at = created_at + interval '1 microsecond';

                UPDATE user_sessions
                SET refresh_token_hash = decode(
                        replace(id::text, '-', '') || replace(id::text, '-', ''),
                        'hex'),
                    revoked_at = COALESCE(revoked_at, GREATEST(created_at, now()))
                WHERE octet_length(refresh_token_hash) <> 32;

                UPDATE user_sessions
                SET revoked_at = GREATEST(created_at, now())
                WHERE replaced_by_session_id IS NOT NULL
                  AND revoked_at IS NULL;

                WITH ranked_active_sessions AS
                (
                    SELECT id,
                           row_number() OVER
                           (
                               PARTITION BY token_family_id
                               ORDER BY created_at DESC, id DESC
                           ) AS family_position
                    FROM user_sessions
                    WHERE revoked_at IS NULL
                )
                UPDATE user_sessions AS session
                SET revoked_at = GREATEST(session.created_at, now())
                FROM ranked_active_sessions AS ranked
                WHERE session.id = ranked.id
                  AND ranked.family_position > 1;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "access_expires_at",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "access_token_hash",
                table: "user_sessions",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "identity_bootstrap_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_bootstrap_state", x => x.id);
                    table.CheckConstraint("ck_identity_bootstrap_state_singleton", "id = 1");
                    table.ForeignKey(
                        name: "fk_identity_bootstrap_state_users_admin_user_id",
                        column: x => x.admin_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO identity_bootstrap_state (id, admin_user_id, completed_at)
                SELECT 1, NULL, now()
                WHERE EXISTS (SELECT 1 FROM users)
                ON CONFLICT (id) DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_user_sessions_access_token_hash",
                table: "user_sessions",
                column: "access_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_user_sessions_active_token_family_id",
                table: "user_sessions",
                column: "token_family_id",
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_access_expiration",
                table: "user_sessions",
                sql: "access_expires_at > created_at AND access_expires_at <= expires_at");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_access_token_hash_length",
                table: "user_sessions",
                sql: "octet_length(access_token_hash) = 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_refresh_token_hash_length",
                table: "user_sessions",
                sql: "octet_length(refresh_token_hash) = 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_replacement",
                table: "user_sessions",
                sql: "replaced_by_session_id IS NULL OR replaced_by_session_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_replacement_requires_revocation",
                table: "user_sessions",
                sql: "replaced_by_session_id IS NULL OR revoked_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_login_account_throttle",
                table: "audit_events",
                columns: new[] { "actor_identifier", "action", "outcome", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_login_ip_throttle",
                table: "audit_events",
                columns: new[] { "ip_address", "action", "outcome", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_bootstrap_state_admin_user_id",
                table: "identity_bootstrap_state",
                column: "admin_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_bootstrap_state");

            migrationBuilder.DropIndex(
                name: "ux_user_sessions_access_token_hash",
                table: "user_sessions");

            migrationBuilder.DropIndex(
                name: "ux_user_sessions_active_token_family_id",
                table: "user_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_access_expiration",
                table: "user_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_access_token_hash_length",
                table: "user_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_refresh_token_hash_length",
                table: "user_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_replacement",
                table: "user_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_replacement_requires_revocation",
                table: "user_sessions");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_login_account_throttle",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_login_ip_throttle",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "access_expires_at",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "access_token_hash",
                table: "user_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_refresh_token_hash_length",
                table: "user_sessions",
                sql: "octet_length(refresh_token_hash) >= 32");
        }
    }
}
