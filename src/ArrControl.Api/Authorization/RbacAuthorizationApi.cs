using System.Security.Claims;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Connections;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Authorization;
using ArrControl.Infrastructure.Connections;
using ArrControl.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Authorization;

public static class RbacPolicyNames
{
    private const string AnyScopePrefix = "rbac:any:";
    private const string GlobalPrefix = "rbac:global:";

    public static string AnyScope(string permissionCode) => AnyScopePrefix + permissionCode;

    public static string Global(string permissionCode) => GlobalPrefix + permissionCode;
}

public static class RbacAuthorizationApi
{
    private const long MutationRequestSizeLimit = 32 * 1024;

    public static IServiceCollection AddRbacAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IRbacGrantStore, EfRbacGrantStore>();
        services.AddScoped<IRbacAdministrationStore, EfRbacAdministrationStore>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            RbacPermissionAuthorizationHandler>();
        services.AddScoped<RbacAuthorizationService>();
        services.AddScoped<RbacAdministrationService>();
        services.AddScoped<IUserPreferencesStore, EfUserPreferencesStore>();
        services.AddScoped<UserPreferencesService>();
        services.AddScoped<IScopedInstanceReadStore, EfScopedInstanceReadStore>();
        services.AddScoped<ScopedInstanceReadService>();
        services.AddAuthorization(options =>
        {
            foreach (var permissionCode in RbacPermissions.All)
            {
                options.AddPolicy(
                    RbacPolicyNames.AnyScope(permissionCode),
                    policy => policy
                        .RequireAuthenticatedUser()
                        .AddRequirements(new RbacPermissionRequirement(
                            permissionCode,
                            RequireGlobal: false)));
                options.AddPolicy(
                    RbacPolicyNames.Global(permissionCode),
                    policy => policy
                        .RequireAuthenticatedUser()
                        .AddRequirements(new RbacPermissionRequirement(
                            permissionCode,
                            RequireGlobal: true)));
            }
        });
        return services;
    }

    public static IEndpointRouteBuilder MapRbacAuthorization(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/auth/me", GetCurrentAuthorizationAsync)
            .WithName("getCurrentAuthorization")
            .WithTags("Authentication")
            .Produces<CurrentAuthorizationResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        endpoints.MapPut("/api/v1/auth/preferences", UpdatePreferencesAsync)
            .WithName("updateCurrentUserPreferences")
            .WithTags("Authentication")
            .Accepts<UpdateUserPreferencesRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024))
            .Produces<UserPreferencesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var administration = endpoints.MapGroup("/api/v1/authorization")
            .WithTags("Authorization")
            .RequireAuthorization(RbacPolicyNames.Global(RbacPermissions.AuthorizationManage));

        administration.MapGet("/permissions", GetPermissions)
            .WithName("listAuthorizationPermissions")
            .Produces<PermissionDefinition[]>();
        administration.MapGet("/roles", ListRolesAsync)
            .WithName("listAuthorizationRoles")
            .Produces<AuthorizationRole[]>();
        administration.MapGet("/roles/{roleId:guid}", GetRoleAsync)
            .WithName("getAuthorizationRole")
            .Produces<AuthorizationRole>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        administration.MapPut("/roles/{roleId:guid}", UpsertRoleAsync)
            .WithName("upsertAuthorizationRole")
            .Accepts<UpsertAuthorizationRoleRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MutationRequestSizeLimit))
            .Produces<AuthorizationRole>(StatusCodes.Status200OK)
            .Produces<AuthorizationRole>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        administration.MapDelete("/roles/{roleId:guid}", DeleteRoleAsync)
            .WithName("deleteAuthorizationRole")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        administration.MapGet("/users", ListUsersAsync)
            .WithName("listAuthorizationUsers")
            .Produces<AuthorizationUser[]>();
        administration.MapGet("/users/{userId:guid}/role-assignments", GetAssignmentsAsync)
            .WithName("listUserRoleAssignments")
            .Produces<RoleAssignment[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        administration.MapPut("/users/{userId:guid}/role-assignments", ReplaceAssignmentsAsync)
            .WithName("replaceUserRoleAssignments")
            .Accepts<ReplaceRoleAssignmentsRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MutationRequestSizeLimit))
            .Produces<RoleAssignment[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        administration.MapGet("/instance-groups", ListInstanceGroupsAsync)
            .WithName("listAuthorizationInstanceGroups")
            .Produces<AuthorizationInstanceGroup[]>();

        return endpoints;
    }

    private static async Task<IResult> GetCurrentAuthorizationAsync(
        HttpContext context,
        RbacAuthorizationService authorizationService,
        UserPreferencesService preferencesService,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                "authentication_required");
        }

        var snapshot = await authorizationService.GetSnapshotAsync(
            identity.UserId,
            identity.SessionId,
            cancellationToken);
        var preferences = await preferencesService.GetAsync(identity.UserId, cancellationToken);
        if (preferences is null)
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                "authentication_required");
        }
        var permissions = snapshot.Grants
            .Select(grant => new PermissionGrantResponse(
                grant.PermissionCode,
                grant.IsGlobal,
                grant.InstanceGroupIds.Order().ToArray()))
            .ToArray();
        return Results.Ok(new CurrentAuthorizationResponse(
            identity.UserId,
            identity.Email,
            identity.AuthenticationMethod,
            preferences.Locale,
            preferences.TimeZone,
            permissions));
    }

    private static async Task<IResult> UpdatePreferencesAsync(
        UpdateUserPreferencesRequest request,
        HttpContext context,
        UserPreferencesService preferencesService,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                "authentication_required");
        }

        try
        {
            var preferences = await preferencesService.UpdateAsync(
                actor,
                request.Locale,
                request.TimeZone,
                cancellationToken);
            return preferences is null
                ? AuthApiProblem.Create(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Authentication required.",
                    "authentication_required")
                : Results.Ok(new UserPreferencesResponse(
                    preferences.Locale,
                    preferences.TimeZone));
        }
        catch (UserPreferenceValidationException exception)
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status400BadRequest,
                "The user preferences are invalid.",
                exception.Code);
        }
    }

    private static IResult GetPermissions() => Results.Ok(RbacPermissions.All
        .Order(StringComparer.Ordinal)
        .Select(code => new PermissionDefinition(code))
        .ToArray());

    private static async Task<IResult> ListRolesAsync(
        RbacAdministrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListRolesAsync(cancellationToken));

    private static async Task<IResult> GetRoleAsync(
        Guid roleId,
        HttpContext context,
        RbacAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var role = await service.GetRoleAsync(roleId, cancellationToken);
            return role is null
                ? Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "Role not found.",
                    "role_not_found")
                : Results.Ok(role);
        }
        catch (RbacAdministrationValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static async Task<IResult> ListUsersAsync(
        RbacAdministrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListUsersAsync(cancellationToken));

    private static async Task<IResult> ListInstanceGroupsAsync(
        RbacAdministrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListInstanceGroupsAsync(cancellationToken));

    private static async Task<IResult> GetAssignmentsAsync(
        Guid userId,
        HttpContext context,
        RbacAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var assignments = await service.GetManualRoleAssignmentsAsync(
                userId,
                cancellationToken);
            return assignments is null
                ? Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "User not found.",
                    "user_not_found")
                : Results.Ok(assignments);
        }
        catch (RbacAdministrationValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static async Task<IResult> UpsertRoleAsync(
        Guid roleId,
        UpsertAuthorizationRoleRequest request,
        HttpContext context,
        RbacAdministrationService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var result = await service.UpsertRoleAsync(
                actor,
                roleId,
                request.Name,
                request.Permissions,
                cancellationToken);
            return result.Status switch
            {
                UpsertAuthorizationRoleStatus.Created => Results.Created(
                    $"/api/v1/authorization/roles/{roleId}",
                    result.Role),
                UpsertAuthorizationRoleStatus.Updated
                    or UpsertAuthorizationRoleStatus.Unchanged => Results.Ok(result.Role),
                UpsertAuthorizationRoleStatus.Forbidden => AccessDenied(context),
                UpsertAuthorizationRoleStatus.NameConflict => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "A role with that name already exists.",
                    "role_name_conflict"),
                UpsertAuthorizationRoleStatus.SystemRoleImmutable => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "System roles are immutable.",
                    "system_role_immutable"),
                UpsertAuthorizationRoleStatus.AuthorizationLockout => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "The last global authorization manager cannot be removed.",
                    "authorization_lockout"),
                _ => throw new InvalidOperationException("The role mutation result is invalid."),
            };
        }
        catch (RbacAdministrationValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static async Task<IResult> DeleteRoleAsync(
        Guid roleId,
        HttpContext context,
        RbacAdministrationService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var status = await service.DeleteRoleAsync(actor, roleId, cancellationToken);
            return status switch
            {
                DeleteAuthorizationRoleStatus.Deleted => Results.NoContent(),
                DeleteAuthorizationRoleStatus.Absent => Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "Role not found.",
                    "role_not_found"),
                DeleteAuthorizationRoleStatus.Forbidden => AccessDenied(context),
                DeleteAuthorizationRoleStatus.SystemRoleImmutable => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "System roles are immutable.",
                    "system_role_immutable"),
                DeleteAuthorizationRoleStatus.AuthorizationLockout => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "The last global authorization manager cannot be removed.",
                    "authorization_lockout"),
                _ => throw new InvalidOperationException("The role deletion result is invalid."),
            };
        }
        catch (RbacAdministrationValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static async Task<IResult> ReplaceAssignmentsAsync(
        Guid userId,
        ReplaceRoleAssignmentsRequest request,
        HttpContext context,
        RbacAdministrationService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var assignments = request.Assignments?
                .Select(value => new RoleAssignmentInput(value.RoleId, value.InstanceGroupId))
                .ToArray();
            var result = await service.ReplaceManualRoleAssignmentsAsync(
                actor,
                userId,
                assignments,
                cancellationToken);
            return result.Status switch
            {
                ReplaceRoleAssignmentsStatus.Updated
                    or ReplaceRoleAssignmentsStatus.Unchanged => Results.Ok(result.Assignments),
                ReplaceRoleAssignmentsStatus.Forbidden => AccessDenied(context),
                ReplaceRoleAssignmentsStatus.UserNotFound => Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "User not found.",
                    "user_not_found"),
                ReplaceRoleAssignmentsStatus.RoleNotFound => Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "Role not found.",
                    "role_not_found"),
                ReplaceRoleAssignmentsStatus.InstanceGroupNotFound => Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "Instance group not found.",
                    "instance_group_not_found"),
                ReplaceRoleAssignmentsStatus.AuthorizationLockout => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "The last global authorization manager cannot be removed.",
                    "authorization_lockout"),
                _ => throw new InvalidOperationException(
                    "The role-assignment mutation result is invalid."),
            };
        }
        catch (RbacAdministrationValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static IResult ValidationProblem(
        HttpContext context,
        RbacAdministrationValidationException exception) =>
        Problem(
            context,
            StatusCodes.Status400BadRequest,
            "The authorization request is invalid.",
            exception.Code);

    private static IResult AccessDenied(HttpContext context) =>
        Problem(context, StatusCodes.Status403Forbidden, "Access denied.", "access_denied");

    private static IResult Problem(
        HttpContext context,
        int statusCode,
        string title,
        string code) =>
        AuthApiProblem.Create(context, statusCode, title, code);
}

public sealed record PermissionDefinition(string Code);

public sealed record PermissionGrantResponse(
    string Code,
    bool Global,
    IReadOnlyList<Guid> InstanceGroupIds);

public sealed record CurrentAuthorizationResponse(
    Guid UserId,
    string Email,
    string AuthenticationMethod,
    string Locale,
    string TimeZone,
    IReadOnlyList<PermissionGrantResponse> Permissions);

public sealed record UpdateUserPreferencesRequest(string? Locale, string? TimeZone);

public sealed record UserPreferencesResponse(string Locale, string TimeZone);

public sealed record UpsertAuthorizationRoleRequest(
    string? Name,
    IReadOnlyList<string>? Permissions);

public sealed record RoleAssignmentRequest(
    Guid RoleId,
    Guid? InstanceGroupId);

public sealed record ReplaceRoleAssignmentsRequest(
    IReadOnlyList<RoleAssignmentRequest>? Assignments);

public sealed record RbacPermissionRequirement(
    string PermissionCode,
    bool RequireGlobal) : IAuthorizationRequirement;

public sealed class RbacPermissionAuthorizationHandler(
    RbacAuthorizationService authorizationService)
    : AuthorizationHandler<RbacPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RbacPermissionRequirement requirement)
    {
        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionIdValue = context.User.FindFirstValue(LocalIdentityConstants.SessionIdClaim);
        if (!Guid.TryParse(userIdValue, out var userId)
            || !Guid.TryParse(sessionIdValue, out var sessionId))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;
        var authorized = requirement.RequireGlobal
            ? await authorizationService.HasGlobalAsync(
                userId,
                sessionId,
                requirement.PermissionCode,
                cancellationToken)
            : await authorizationService.HasAnyScopeAsync(
                userId,
                sessionId,
                requirement.PermissionCode,
                cancellationToken);
        if (authorized)
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed record RbacRequestIdentity(
    Guid UserId,
    Guid SessionId,
    string Email,
    string AuthenticationMethod);

internal static class RbacHttpContext
{
    public static bool TryGetIdentity(
        HttpContext context,
        out RbacRequestIdentity identity)
    {
        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionIdValue = context.User.FindFirstValue(LocalIdentityConstants.SessionIdClaim);
        var email = context.User.FindFirstValue(ClaimTypes.Email);
        var authenticationMethod = context.User.FindFirstValue(
            LocalIdentityConstants.AuthenticationMethodClaim);
        if (!Guid.TryParse(userIdValue, out var userId)
            || !Guid.TryParse(sessionIdValue, out var sessionId)
            || string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(authenticationMethod))
        {
            identity = null!;
            return false;
        }

        identity = new RbacRequestIdentity(
            userId,
            sessionId,
            email,
            authenticationMethod);
        return true;
    }

    public static bool TryGetActor(HttpContext context, out RbacActorContext actor)
    {
        if (!TryGetIdentity(context, out var identity))
        {
            actor = null!;
            return false;
        }

        actor = new RbacActorContext(
            identity.UserId,
            identity.SessionId,
            identity.Email,
            AuthenticationHttpContext.CreateRequestContext(context));
        return true;
    }
}
