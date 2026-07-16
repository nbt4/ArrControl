using System.Collections.Frozen;

namespace ArrControl.Application.Authorization;

public static class RbacPermissions
{
    public const string InstancesRead = "instances.read";
    public const string InstancesManage = "instances.manage";
    public const string LibraryRead = "library.read";
    public const string SearchExecute = "search.execute";
    public const string QueueManage = "queue.manage";
    public const string TasksExecute = "tasks.execute";
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";
    public const string SettingsManage = "settings.manage";
    public const string AuthorizationManage = "authorization.manage";

    private static readonly FrozenSet<string> KnownPermissions = new[]
    {
        InstancesRead,
        InstancesManage,
        LibraryRead,
        SearchExecute,
        QueueManage,
        TasksExecute,
        UsersManage,
        AuditRead,
        SettingsManage,
        AuthorizationManage,
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> All => KnownPermissions;

    public static bool IsKnown(string? permissionCode) =>
        permissionCode is not null && KnownPermissions.Contains(permissionCode);
}

public sealed record RbacSystemRoleDefinition(
    string Name,
    string NormalizedName,
    IReadOnlySet<string> Permissions);

public static class RbacSystemRoles
{
    public const string Administrator = "Administrator";
    public const string AdministratorNormalized = "ADMINISTRATOR";
    public const string Operator = "Operator";
    public const string OperatorNormalized = "OPERATOR";
    public const string Viewer = "Viewer";
    public const string ViewerNormalized = "VIEWER";

    private static readonly RbacSystemRoleDefinition[] RoleDefinitions =
    [
        new(
            Administrator,
            AdministratorNormalized,
            RbacPermissions.All),
        new(
            Operator,
            OperatorNormalized,
            new[]
            {
                RbacPermissions.InstancesRead,
                RbacPermissions.LibraryRead,
                RbacPermissions.SearchExecute,
                RbacPermissions.QueueManage,
                RbacPermissions.TasksExecute,
            }.ToFrozenSet(StringComparer.Ordinal)),
        new(
            Viewer,
            ViewerNormalized,
            new[]
            {
                RbacPermissions.InstancesRead,
                RbacPermissions.LibraryRead,
            }.ToFrozenSet(StringComparer.Ordinal)),
    ];

    public static IReadOnlyList<RbacSystemRoleDefinition> All { get; } =
        Array.AsReadOnly(RoleDefinitions);
}
