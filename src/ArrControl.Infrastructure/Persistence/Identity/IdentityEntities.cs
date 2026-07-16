using ArrControl.Infrastructure.Persistence.Connections;

namespace ArrControl.Infrastructure.Persistence.Identity;

public sealed class UserEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public string? PasswordHash { get; set; }
    public required string Locale { get; set; }
    public required string TimeZone { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<ExternalIdentityEntity> ExternalIdentities { get; } = new List<ExternalIdentityEntity>();
    public ICollection<UserRoleEntity> RoleAssignments { get; } = new List<UserRoleEntity>();
    public ICollection<UserSessionEntity> Sessions { get; } = new List<UserSessionEntity>();
}

public sealed class ExternalIdentityEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public int ClaimsVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAuthenticatedAt { get; set; }
    public UserEntity User { get; set; } = null!;
    public ICollection<ExternalIdentityRoleEntity> RoleAssignments { get; } =
        new List<ExternalIdentityRoleEntity>();
    public ICollection<OidcSessionContextEntity> SessionContexts { get; } =
        new List<OidcSessionContextEntity>();
}

public sealed class RoleEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<RolePermissionEntity> Permissions { get; } = new List<RolePermissionEntity>();
    public ICollection<UserRoleEntity> UserAssignments { get; } = new List<UserRoleEntity>();
    public ICollection<ExternalIdentityRoleEntity> ExternalIdentityAssignments { get; } =
        new List<ExternalIdentityRoleEntity>();
}

public sealed class ExternalIdentityRoleEntity
{
    public Guid ExternalIdentityId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ExternalIdentityEntity ExternalIdentity { get; set; } = null!;
    public RoleEntity Role { get; set; } = null!;
}

public sealed class PermissionEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Code { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<RolePermissionEntity> Roles { get; } = new List<RolePermissionEntity>();
}

public sealed class RolePermissionEntity
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
    public RoleEntity Role { get; set; } = null!;
    public PermissionEntity Permission { get; set; } = null!;
}

public sealed class UserRoleEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? InstanceGroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public UserEntity User { get; set; } = null!;
    public RoleEntity Role { get; set; } = null!;
    public InstanceGroupEntity? InstanceGroup { get; set; }
}

public sealed class UserSessionEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid TokenFamilyId { get; set; } = Guid.CreateVersion7();
    public required byte[] AccessTokenHash { get; set; }
    public DateTimeOffset AccessExpiresAt { get; set; }
    public required byte[] RefreshTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedBySessionId { get; set; }
    public required string AuthenticationMethod { get; set; }
    public UserEntity User { get; set; } = null!;
    public UserSessionEntity? ReplacedBySession { get; set; }
}

public sealed class OidcSessionContextEntity
{
    public Guid TokenFamilyId { get; set; }
    public Guid ExternalIdentityId { get; set; }
    public required string ProtectedIdToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public ExternalIdentityEntity ExternalIdentity { get; set; } = null!;
}

public sealed class IdentityBootstrapStateEntity
{
    public short Id { get; set; } = 1;
    public Guid? AdminUserId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public UserEntity? AdminUser { get; set; }
}
