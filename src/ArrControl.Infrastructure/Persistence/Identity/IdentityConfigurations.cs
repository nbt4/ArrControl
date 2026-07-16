using ArrControl.Application.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArrControl.Infrastructure.Persistence.Identity;

internal sealed class UserConfiguration : IEntityTypeConfiguration<UserEntity>
{
    public void Configure(EntityTypeBuilder<UserEntity> entity)
    {
        entity.ToTable("users");
        entity.HasKey(x => x.Id).HasName("pk_users");
        entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
        entity.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
        entity.Property(x => x.PasswordHash).HasMaxLength(1024);
        entity.Property(x => x.Locale).HasMaxLength(16).IsRequired();
        entity.Property(x => x.TimeZone).HasMaxLength(128).IsRequired();
        entity.Property(x => x.State).HasMaxLength(32).IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("ux_users_normalized_email");
    }
}

internal sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentityEntity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentityEntity> entity)
    {
        entity.ToTable(
            "external_identities",
            table => table.HasCheckConstraint(
                "ck_external_identities_claims_version_nonnegative",
                "claims_version >= 0"));
        entity.HasKey(x => x.Id).HasName("pk_external_identities");
        entity.Property(x => x.Issuer).HasMaxLength(2048).IsRequired();
        entity.Property(x => x.Subject).HasMaxLength(512).IsRequired();
        entity.Property(x => x.ClaimsVersion).HasDefaultValue(0);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => new { x.Issuer, x.Subject })
            .IsUnique()
            .HasDatabaseName("ux_external_identities_issuer_subject");
        entity.HasIndex(x => x.UserId).HasDatabaseName("ix_external_identities_user_id");
        entity.HasOne(x => x.User)
            .WithMany(x => x.ExternalIdentities)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_external_identities_users_user_id");
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<RoleEntity>
{
    public void Configure(EntityTypeBuilder<RoleEntity> entity)
    {
        entity.ToTable("roles");
        entity.HasKey(x => x.Id).HasName("pk_roles");
        entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
        entity.Property(x => x.NormalizedName).HasMaxLength(120).IsRequired();
        entity.Property(x => x.IsSystem).HasDefaultValue(false);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.NormalizedName)
            .IsUnique()
            .HasDatabaseName("ux_roles_normalized_name");
    }
}

internal sealed class ExternalIdentityRoleConfiguration
    : IEntityTypeConfiguration<ExternalIdentityRoleEntity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentityRoleEntity> entity)
    {
        entity.ToTable("external_identity_roles");
        entity.HasKey(x => new { x.ExternalIdentityId, x.RoleId })
            .HasName("pk_external_identity_roles");
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.RoleId).HasDatabaseName("ix_external_identity_roles_role_id");
        entity.HasOne(x => x.ExternalIdentity)
            .WithMany(x => x.RoleAssignments)
            .HasForeignKey(x => x.ExternalIdentityId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_external_identity_roles_external_identities_external_identity_id");
        entity.HasOne(x => x.Role)
            .WithMany(x => x.ExternalIdentityAssignments)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_external_identity_roles_roles_role_id");
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<PermissionEntity>
{
    public void Configure(EntityTypeBuilder<PermissionEntity> entity)
    {
        entity.ToTable("permissions");
        entity.HasKey(x => x.Id).HasName("pk_permissions");
        entity.Property(x => x.Code).HasMaxLength(160).IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_permissions_code");
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermissionEntity>
{
    public void Configure(EntityTypeBuilder<RolePermissionEntity> entity)
    {
        entity.ToTable("role_permissions");
        entity.HasKey(x => new { x.RoleId, x.PermissionId }).HasName("pk_role_permissions");
        entity.HasIndex(x => x.PermissionId).HasDatabaseName("ix_role_permissions_permission_id");
        entity.HasOne(x => x.Role)
            .WithMany(x => x.Permissions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_role_permissions_roles_role_id");
        entity.HasOne(x => x.Permission)
            .WithMany(x => x.Roles)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_role_permissions_permissions_permission_id");
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRoleEntity>
{
    public void Configure(EntityTypeBuilder<UserRoleEntity> entity)
    {
        entity.ToTable("user_roles");
        entity.HasKey(x => x.Id).HasName("pk_user_roles");
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => new { x.UserId, x.RoleId })
            .IsUnique()
            .HasFilter("instance_group_id IS NULL")
            .HasDatabaseName("ux_user_roles_user_id_role_id_global");
        entity.HasIndex(x => new { x.UserId, x.RoleId, x.InstanceGroupId })
            .IsUnique()
            .HasFilter("instance_group_id IS NOT NULL")
            .HasDatabaseName("ux_user_roles_user_id_role_id_instance_group_id");
        entity.HasIndex(x => x.RoleId).HasDatabaseName("ix_user_roles_role_id");
        entity.HasIndex(x => x.InstanceGroupId).HasDatabaseName("ix_user_roles_instance_group_id");
        entity.HasOne(x => x.User)
            .WithMany(x => x.RoleAssignments)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_user_roles_users_user_id");
        entity.HasOne(x => x.Role)
            .WithMany(x => x.UserAssignments)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_user_roles_roles_role_id");
        entity.HasOne(x => x.InstanceGroup)
            .WithMany()
            .HasForeignKey(x => x.InstanceGroupId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_user_roles_instance_groups_instance_group_id");
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSessionEntity>
{
    public void Configure(EntityTypeBuilder<UserSessionEntity> entity)
    {
        entity.ToTable(
            "user_sessions",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_user_sessions_access_token_hash_length",
                    "octet_length(access_token_hash) = 32");
                table.HasCheckConstraint(
                    "ck_user_sessions_refresh_token_hash_length",
                    "octet_length(refresh_token_hash) = 32");
                table.HasCheckConstraint(
                    "ck_user_sessions_expiration",
                    "expires_at > created_at");
                table.HasCheckConstraint(
                    "ck_user_sessions_access_expiration",
                    "access_expires_at > created_at AND access_expires_at <= expires_at");
                table.HasCheckConstraint(
                    "ck_user_sessions_last_used_at",
                    "last_used_at IS NULL OR last_used_at >= created_at");
                table.HasCheckConstraint(
                    "ck_user_sessions_revoked_at",
                    "revoked_at IS NULL OR revoked_at >= created_at");
                table.HasCheckConstraint(
                    "ck_user_sessions_replacement",
                    "replaced_by_session_id IS NULL OR replaced_by_session_id <> id");
                table.HasCheckConstraint(
                    "ck_user_sessions_replacement_requires_revocation",
                    "replaced_by_session_id IS NULL OR revoked_at IS NOT NULL");
                table.HasCheckConstraint(
                    "ck_user_sessions_authentication_method",
                    "authentication_method IN ('local', 'oidc')");
            });
        entity.HasKey(x => x.Id).HasName("pk_user_sessions");
        entity.Property(x => x.AccessTokenHash).IsRequired();
        entity.Property(x => x.RefreshTokenHash).IsRequired();
        entity.Property(x => x.AuthenticationMethod)
            .HasMaxLength(32)
            .HasDefaultValue("local")
            .IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.AccessTokenHash)
            .IsUnique()
            .HasDatabaseName("ux_user_sessions_access_token_hash");
        entity.HasIndex(x => x.RefreshTokenHash)
            .IsUnique()
            .HasDatabaseName("ux_user_sessions_refresh_token_hash");
        entity.HasIndex(x => new { x.UserId, x.ExpiresAt })
            .HasDatabaseName("ix_user_sessions_user_id_expires_at");
        entity.HasIndex(
                [nameof(UserSessionEntity.TokenFamilyId)],
                "user_sessions_token_family_lookup")
            .HasDatabaseName("ix_user_sessions_token_family_id");
        entity.HasIndex(
                [nameof(UserSessionEntity.TokenFamilyId)],
                "user_sessions_active_token_family")
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ux_user_sessions_active_token_family_id");
        entity.HasIndex(x => x.ReplacedBySessionId)
            .IsUnique()
            .HasFilter("replaced_by_session_id IS NOT NULL")
            .HasDatabaseName("ux_user_sessions_replaced_by_session_id");
        entity.HasOne(x => x.User)
            .WithMany(x => x.Sessions)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_user_sessions_users_user_id");
        entity.HasOne(x => x.ReplacedBySession)
            .WithMany()
            .HasForeignKey(x => x.ReplacedBySessionId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_user_sessions_user_sessions_replaced_by_session_id");
    }
}

internal sealed class OidcSessionContextConfiguration
    : IEntityTypeConfiguration<OidcSessionContextEntity>
{
    public void Configure(EntityTypeBuilder<OidcSessionContextEntity> entity)
    {
        entity.ToTable(
            "oidc_session_contexts",
            table => table.HasCheckConstraint(
                "ck_oidc_session_contexts_expiration",
                "expires_at > created_at"));
        entity.HasKey(x => x.TokenFamilyId).HasName("pk_oidc_session_contexts");
        entity.Property(x => x.ProtectedIdToken)
            .HasMaxLength(OidcIdentityLimits.MaximumProtectedIdTokenLength)
            .IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.ExternalIdentityId)
            .HasDatabaseName("ix_oidc_session_contexts_external_identity_id");
        entity.HasOne(x => x.ExternalIdentity)
            .WithMany(x => x.SessionContexts)
            .HasForeignKey(x => x.ExternalIdentityId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_oidc_session_contexts_external_identities_external_identity_id");
    }
}

internal sealed class IdentityBootstrapStateConfiguration : IEntityTypeConfiguration<IdentityBootstrapStateEntity>
{
    public void Configure(EntityTypeBuilder<IdentityBootstrapStateEntity> entity)
    {
        entity.ToTable(
            "identity_bootstrap_state",
            table => table.HasCheckConstraint("ck_identity_bootstrap_state_singleton", "id = 1"));
        entity.HasKey(x => x.Id).HasName("pk_identity_bootstrap_state");
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.HasIndex(x => x.AdminUserId)
            .HasDatabaseName("ix_identity_bootstrap_state_admin_user_id");
        entity.HasOne(x => x.AdminUser)
            .WithMany()
            .HasForeignKey(x => x.AdminUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_identity_bootstrap_state_users_admin_user_id");
    }
}
