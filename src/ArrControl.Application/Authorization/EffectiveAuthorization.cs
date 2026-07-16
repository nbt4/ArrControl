using System.Collections.Frozen;

namespace ArrControl.Application.Authorization;

public sealed record InstanceGroupAuthorization(
    string PermissionCode,
    bool IsGlobal,
    IReadOnlySet<Guid> InstanceGroupIds);

public sealed class EffectiveAuthorization
{
    private static readonly FrozenSet<Guid> NoInstanceGroups =
        Array.Empty<Guid>().ToFrozenSet();

    private readonly FrozenDictionary<string, InstanceGroupAuthorization> grantsByPermission;

    private EffectiveAuthorization(
        FrozenDictionary<string, InstanceGroupAuthorization> grantsByPermission)
    {
        this.grantsByPermission = grantsByPermission;
        Grants = grantsByPermission.Values
            .OrderBy(grant => grant.PermissionCode, StringComparer.Ordinal)
            .ToArray();
    }

    public static EffectiveAuthorization Empty { get; } =
        new(new Dictionary<string, InstanceGroupAuthorization>(StringComparer.Ordinal)
            .ToFrozenDictionary(StringComparer.Ordinal));

    public IReadOnlyList<InstanceGroupAuthorization> Grants { get; }

    public static EffectiveAuthorization Create(
        IEnumerable<StoredPermissionGrant> storedGrants)
    {
        ArgumentNullException.ThrowIfNull(storedGrants);

        var builders = new Dictionary<string, GrantBuilder>(StringComparer.Ordinal);
        foreach (var storedGrant in storedGrants)
        {
            if (!RbacPermissions.IsKnown(storedGrant.PermissionCode))
            {
                continue;
            }

            if (storedGrant.InstanceGroupId == Guid.Empty)
            {
                continue;
            }

            if (!builders.TryGetValue(storedGrant.PermissionCode, out var builder))
            {
                builder = new GrantBuilder();
                builders.Add(storedGrant.PermissionCode, builder);
            }

            if (storedGrant.InstanceGroupId is null)
            {
                builder.IsGlobal = true;
                builder.InstanceGroupIds.Clear();
            }
            else if (!builder.IsGlobal)
            {
                builder.InstanceGroupIds.Add(storedGrant.InstanceGroupId.Value);
            }
        }

        if (builders.Count == 0)
        {
            return Empty;
        }

        var effectiveGrants = builders.ToFrozenDictionary(
            pair => pair.Key,
            pair => new InstanceGroupAuthorization(
                pair.Key,
                pair.Value.IsGlobal,
                pair.Value.IsGlobal
                    ? NoInstanceGroups
                    : pair.Value.InstanceGroupIds.ToFrozenSet()),
            StringComparer.Ordinal);
        return new EffectiveAuthorization(effectiveGrants);
    }

    public bool HasAnyScope(string? permissionCode) =>
        permissionCode is not null && grantsByPermission.ContainsKey(permissionCode);

    public bool HasGlobal(string? permissionCode) =>
        permissionCode is not null
        && grantsByPermission.TryGetValue(permissionCode, out var grant)
        && grant.IsGlobal;

    public bool HasInstanceGroup(string? permissionCode, Guid? instanceGroupId)
    {
        if (instanceGroupId is null)
        {
            return HasGlobal(permissionCode);
        }

        return permissionCode is not null
            && instanceGroupId != Guid.Empty
            && grantsByPermission.TryGetValue(permissionCode, out var grant)
            && (grant.IsGlobal || grant.InstanceGroupIds.Contains(instanceGroupId.Value));
    }

    private sealed class GrantBuilder
    {
        public bool IsGlobal { get; set; }

        public HashSet<Guid> InstanceGroupIds { get; } = [];
    }
}
