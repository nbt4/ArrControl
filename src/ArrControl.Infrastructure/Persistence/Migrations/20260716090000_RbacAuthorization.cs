using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RbacAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $arrcontrol$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM roles
                        WHERE normalized_name IN ('ADMINISTRATOR', 'OPERATOR', 'VIEWER')
                          AND NOT is_system
                    ) THEN
                        RAISE EXCEPTION
                            'RBAC system-role migration cannot replace a non-system Administrator, Operator, or Viewer role.';
                    END IF;
                END
                $arrcontrol$;

                INSERT INTO permissions (id, code, created_at)
                VALUES
                    ('019814c4-1a40-7000-8000-000000000001', 'authorization.manage', now()),
                    ('019814c4-1a40-7000-8000-000000000002', 'instances.read', now()),
                    ('019814c4-1a40-7000-8000-000000000003', 'instances.manage', now()),
                    ('019814c4-1a40-7000-8000-000000000004', 'library.read', now()),
                    ('019814c4-1a40-7000-8000-000000000005', 'search.execute', now()),
                    ('019814c4-1a40-7000-8000-000000000006', 'queue.manage', now()),
                    ('019814c4-1a40-7000-8000-000000000007', 'tasks.execute', now()),
                    ('019814c4-1a40-7000-8000-000000000008', 'users.manage', now()),
                    ('019814c4-1a40-7000-8000-000000000009', 'audit.read', now()),
                    ('019814c4-1a40-7000-8000-00000000000a', 'settings.manage', now())
                ON CONFLICT (code) DO NOTHING;

                INSERT INTO roles (id, name, normalized_name, is_system, created_at)
                VALUES
                    ('019814c4-1a40-7001-8000-000000000001', 'Administrator', 'ADMINISTRATOR', TRUE, now()),
                    ('019814c4-1a40-7001-8000-000000000002', 'Operator', 'OPERATOR', TRUE, now()),
                    ('019814c4-1a40-7001-8000-000000000003', 'Viewer', 'VIEWER', TRUE, now())
                ON CONFLICT (normalized_name) DO UPDATE
                SET name = EXCLUDED.name,
                    is_system = TRUE;

                DELETE FROM role_permissions AS role_permission
                USING roles AS role
                WHERE role_permission.role_id = role.id
                  AND role.normalized_name IN ('ADMINISTRATOR', 'OPERATOR', 'VIEWER');

                WITH role_permission_matrix (normalized_role_name, permission_code) AS (
                    VALUES
                        ('ADMINISTRATOR', 'authorization.manage'),
                        ('ADMINISTRATOR', 'instances.read'),
                        ('ADMINISTRATOR', 'instances.manage'),
                        ('ADMINISTRATOR', 'library.read'),
                        ('ADMINISTRATOR', 'search.execute'),
                        ('ADMINISTRATOR', 'queue.manage'),
                        ('ADMINISTRATOR', 'tasks.execute'),
                        ('ADMINISTRATOR', 'users.manage'),
                        ('ADMINISTRATOR', 'audit.read'),
                        ('ADMINISTRATOR', 'settings.manage'),
                        ('OPERATOR', 'instances.read'),
                        ('OPERATOR', 'library.read'),
                        ('OPERATOR', 'search.execute'),
                        ('OPERATOR', 'queue.manage'),
                        ('OPERATOR', 'tasks.execute'),
                        ('VIEWER', 'instances.read'),
                        ('VIEWER', 'library.read')
                )
                INSERT INTO role_permissions (role_id, permission_id)
                SELECT role.id, permission.id
                FROM role_permission_matrix AS matrix
                INNER JOIN roles AS role
                    ON role.normalized_name = matrix.normalized_role_name
                INNER JOIN permissions AS permission
                    ON permission.code = matrix.permission_code
                ON CONFLICT (role_id, permission_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ArrControl migrations are forward-only. Retaining the catalog and mappings avoids
            // cascading deletion of existing manual and OIDC role assignments during rollback.
        }
    }
}
