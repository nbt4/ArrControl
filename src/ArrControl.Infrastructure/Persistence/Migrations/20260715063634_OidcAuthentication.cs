using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OidcAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "authentication_method",
                table: "user_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "local");

            migrationBuilder.CreateTable(
                name: "external_identity_roles",
                columns: table => new
                {
                    external_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_identity_roles", x => new { x.external_identity_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_external_identity_roles_external_identities_external_identity_id",
                        column: x => x.external_identity_id,
                        principalTable: "external_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identity_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oidc_session_contexts",
                columns: table => new
                {
                    token_family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protected_id_token = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oidc_session_contexts", x => x.token_family_id);
                    table.CheckConstraint("ck_oidc_session_contexts_expiration", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_oidc_session_contexts_external_identities_external_identity_id",
                        column: x => x.external_identity_id,
                        principalTable: "external_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_authentication_method",
                table: "user_sessions",
                sql: "authentication_method IN ('local', 'oidc')");

            migrationBuilder.CreateIndex(
                name: "ix_external_identity_roles_role_id",
                table: "external_identity_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_oidc_session_contexts_external_identity_id",
                table: "oidc_session_contexts",
                column: "external_identity_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_identity_roles");

            migrationBuilder.DropTable(
                name: "oidc_session_contexts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_authentication_method",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "authentication_method",
                table: "user_sessions");
        }
    }
}
