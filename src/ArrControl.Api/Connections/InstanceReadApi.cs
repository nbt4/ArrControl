using System.Security.Claims;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Connections;
using ArrControl.Application.Identity;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Connections;
using ArrControl.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Connections;

public static class InstanceReadApi
{
    private const long InstanceMutationRequestSizeLimit = 16 * 1024;

    public static IServiceCollection AddInstanceManagement(this IServiceCollection services)
    {
        services.AddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
        services.AddSingleton<IOutboundTargetPolicy, OutboundTargetPolicy>();
        services.AddSingleton<IConnectionProbeTransport, SafeConnectionProbeTransport>();
        services.AddSingleton<SafeProviderApiTransport>();
        services.AddSingleton<IProviderApiTransport>(provider =>
            provider.GetRequiredService<SafeProviderApiTransport>());
        services.AddSingleton<IProviderHttpTransport>(provider =>
            provider.GetRequiredService<SafeProviderApiTransport>());
        services.AddSingleton<SonarrClient>();
        services.AddSingleton<RadarrClient>();
        services.AddSingleton<LidarrClient>();
        services.AddSingleton<ReadarrClient>();
        services.AddSingleton<WhisparrClient>();
        services.AddSingleton<ProwlarrClient>();
        services.AddSingleton<BazarrClient>();
        services.AddSingleton<SabnzbdClient>();
        services.AddSingleton<NzbGetClient>();
        services.AddSingleton<QBittorrentClient>();
        services.AddSingleton<TransmissionClient>();
        services.AddSingleton<DelugeClient>();
        services.AddSingleton<PlexClient>();
        services.AddSingleton<JellyfinClient>();
        services.AddSingleton<EmbyClient>();
        services.AddSingleton<OverseerrClient>();
        services.AddSingleton<JellyseerrClient>();
        services.AddSingleton<OmbiClient>();
        services.AddSingleton<ISmtpNotificationTransport, SecureSmtpNotificationTransport>();
        services.AddSingleton<EmailNotificationProvider>();
        services.AddSingleton<GenericWebhookNotificationProvider>();
        services.AddSingleton<DiscordNotificationProvider>();
        services.AddSingleton<SlackNotificationProvider>();
        services.AddSingleton<TeamsNotificationProvider>();
        services.AddSingleton<TelegramNotificationProvider>();
        services.AddSingleton<NtfyNotificationProvider>();
        services.AddSingleton<GotifyNotificationProvider>();
        services.AddSingleton<PushoverNotificationProvider>();
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<EmailNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<GenericWebhookNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<DiscordNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<SlackNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<TeamsNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<TelegramNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<NtfyNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<GotifyNotificationProvider>());
        services.AddSingleton<INotificationProvider>(provider => provider.GetRequiredService<PushoverNotificationProvider>());
        services.AddSingleton<IProviderIndexerClient>(serviceProvider =>
            serviceProvider.GetRequiredService<ProwlarrClient>());
        services.AddSingleton<IProviderSubtitleActivityClient>(serviceProvider =>
            serviceProvider.GetRequiredService<BazarrClient>());
        services.AddSingleton<IProviderMediaServerClient>(serviceProvider =>
            serviceProvider.GetRequiredService<PlexClient>());
        services.AddSingleton<IProviderMediaServerClient>(serviceProvider =>
            serviceProvider.GetRequiredService<JellyfinClient>());
        services.AddSingleton<IProviderMediaServerClient>(serviceProvider =>
            serviceProvider.GetRequiredService<EmbyClient>());
        services.AddSingleton<IProviderRequestClient>(serviceProvider =>
            serviceProvider.GetRequiredService<OverseerrClient>());
        services.AddSingleton<IProviderRequestClient>(serviceProvider =>
            serviceProvider.GetRequiredService<JellyseerrClient>());
        services.AddSingleton<IProviderRequestClient>(serviceProvider =>
            serviceProvider.GetRequiredService<OmbiClient>());
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(
                serviceProvider.GetRequiredService<SonarrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(
                serviceProvider.GetRequiredService<RadarrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<LidarrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<ReadarrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<WhisparrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<ProwlarrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<BazarrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<SabnzbdClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<NzbGetClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<QBittorrentClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<TransmissionClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<DelugeClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<PlexClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<JellyfinClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<EmbyClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<OverseerrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<JellyseerrClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProviderConnectionAdapter>(serviceProvider =>
            new ArrProviderConnectionAdapter(serviceProvider.GetRequiredService<OmbiClient>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IInstanceManagementStore, EfInstanceManagementStore>();
        services.AddScoped<InstanceManagementService>();
        return services;
    }

    public static IEndpointRouteBuilder MapInstanceReads(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/instances", ListInstancesAsync)
            .WithName("listInstances")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesRead))
            .Produces<VisibleInstance[]>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        endpoints.MapGet("/api/v1/instances/{instanceId:guid}", GetInstanceAsync)
            .WithName("getInstance")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesRead))
            .Produces<InstanceDetails>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost("/api/v1/instances", CreateInstanceAsync)
            .WithName("createInstance")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesManage))
            .Accepts<WriteInstanceRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(InstanceMutationRequestSizeLimit))
            .Produces<InstanceDetails>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        endpoints.MapPut("/api/v1/instances/{instanceId:guid}", UpdateInstanceAsync)
            .WithName("updateInstance")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesManage))
            .Accepts<WriteInstanceRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(InstanceMutationRequestSizeLimit))
            .Produces<InstanceDetails>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        endpoints.MapDelete("/api/v1/instances/{instanceId:guid}", DeleteInstanceAsync)
            .WithName("deleteInstance")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesManage))
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost("/api/v1/instances/{instanceId:guid}/probe", ProbeInstanceAsync)
            .WithName("probeInstance")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesManage))
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces<ConnectionProbeObservation>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var groups = endpoints.MapGroup("/api/v1/instance-groups")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.Global(RbacPermissions.InstancesManage));
        groups.MapGet("", ListGroupsAsync)
            .WithName("listInstanceGroups")
            .Produces<InstanceGroupDetails[]>();
        groups.MapPut("/{instanceGroupId:guid}", UpsertGroupAsync)
            .WithName("upsertInstanceGroup")
            .Accepts<WriteInstanceGroupRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024))
            .Produces<InstanceGroupDetails>(StatusCodes.Status200OK)
            .Produces<InstanceGroupDetails>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        groups.MapDelete("/{instanceGroupId:guid}", DeleteGroupAsync)
            .WithName("deleteInstanceGroup")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    private static async Task<IResult> ListInstancesAsync(
        HttpContext context,
        ScopedInstanceReadService service,
        CancellationToken cancellationToken)
    {
        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionIdValue = context.User.FindFirstValue(LocalIdentityConstants.SessionIdClaim);
        if (!Guid.TryParse(userIdValue, out var userId)
            || !Guid.TryParse(sessionIdValue, out var sessionId))
        {
            return AuthenticationRequired(context);
        }

        var instances = await service.ListAsync(userId, sessionId, cancellationToken);
        return instances is null ? AccessDenied(context) : Results.Ok(instances);
    }

    private static async Task<IResult> GetInstanceAsync(
        Guid instanceId,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            return AuthenticationRequired(context);
        }

        try
        {
            var instance = await service.GetAsync(
                identity.UserId,
                identity.SessionId,
                instanceId,
                cancellationToken);
            return instance is null ? InstanceNotFound(context) : Results.Ok(instance);
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static async Task<IResult> CreateInstanceAsync(
        WriteInstanceRequest request,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        var instanceId = Guid.CreateVersion7();
        try
        {
            var result = await service.CreateAsync(
                actor,
                instanceId,
                request.Name,
                request.Kind,
                request.BaseUrl,
                request.Enabled,
                request.InstanceGroupId,
                request.TlsVerificationEnabled,
                request.AllowPrivateNetworkAccess,
                cancellationToken);
            return result.Status switch
            {
                InstanceWriteStatus.Created => Results.Created(
                    $"/api/v1/instances/{instanceId}",
                    result.Instance),
                InstanceWriteStatus.GroupNotFound => GroupNotFound(context),
                InstanceWriteStatus.NameConflict => InstanceConflict(context),
                InstanceWriteStatus.Forbidden => AccessDenied(context),
                _ => throw new InvalidOperationException("The instance create result is invalid."),
            };
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
        catch (OutboundTargetRejectedException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static async Task<IResult> UpdateInstanceAsync(
        Guid instanceId,
        WriteInstanceRequest request,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var result = await service.UpdateAsync(
                actor,
                instanceId,
                request.Name,
                request.Kind,
                request.BaseUrl,
                request.Enabled,
                request.InstanceGroupId,
                request.TlsVerificationEnabled,
                request.AllowPrivateNetworkAccess,
                cancellationToken);
            return result.Status switch
            {
                InstanceWriteStatus.Updated => Results.Ok(result.Instance),
                InstanceWriteStatus.NotFound => InstanceNotFound(context),
                InstanceWriteStatus.GroupNotFound => GroupNotFound(context),
                InstanceWriteStatus.NameConflict => InstanceConflict(context),
                InstanceWriteStatus.Forbidden => AccessDenied(context),
                _ => throw new InvalidOperationException("The instance update result is invalid."),
            };
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
        catch (OutboundTargetRejectedException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static async Task<IResult> DeleteInstanceAsync(
        Guid instanceId,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            return (await service.DeleteAsync(actor, instanceId, cancellationToken)) switch
            {
                InstanceDeleteStatus.Deleted => Results.NoContent(),
                InstanceDeleteStatus.NotFound => InstanceNotFound(context),
                InstanceDeleteStatus.Forbidden => AccessDenied(context),
                _ => throw new InvalidOperationException("The instance delete result is invalid."),
            };
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static async Task<IResult> ProbeInstanceAsync(
        Guid instanceId,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var result = await service.ProbeAsync(actor, instanceId, cancellationToken);
            return result.Status switch
            {
                InstanceProbeStatus.Completed => Results.Ok(result.Probe),
                InstanceProbeStatus.NotFound => InstanceNotFound(context),
                InstanceProbeStatus.Forbidden => AccessDenied(context),
                _ => throw new InvalidOperationException("The instance probe result is invalid."),
            };
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
        catch (OutboundTargetRejectedException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static async Task<IResult> ListGroupsAsync(
        InstanceManagementService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListGroupsAsync(cancellationToken));

    private static async Task<IResult> UpsertGroupAsync(
        Guid instanceGroupId,
        WriteInstanceGroupRequest request,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var result = await service.UpsertGroupAsync(
                actor,
                instanceGroupId,
                request.Name,
                cancellationToken);
            return result.Status switch
            {
                InstanceGroupWriteStatus.Created => Results.Created(
                    $"/api/v1/instance-groups/{instanceGroupId}",
                    result.Group),
                InstanceGroupWriteStatus.Updated => Results.Ok(result.Group),
                InstanceGroupWriteStatus.NameConflict => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "Instance group name already exists.",
                    "instance_group_name_conflict"),
                InstanceGroupWriteStatus.NotFound => AccessDenied(context),
                _ => throw new InvalidOperationException("The instance-group write result is invalid."),
            };
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static async Task<IResult> DeleteGroupAsync(
        Guid instanceGroupId,
        HttpContext context,
        InstanceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            return (await service.DeleteGroupAsync(actor, instanceGroupId, cancellationToken)) switch
            {
                InstanceGroupDeleteStatus.Deleted => Results.NoContent(),
                InstanceGroupDeleteStatus.NotFound => Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "Instance group not found.",
                    "instance_group_not_found"),
                InstanceGroupDeleteStatus.InUse => Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "Instance group is still in use.",
                    "instance_group_in_use"),
                _ => throw new InvalidOperationException("The instance-group delete result is invalid."),
            };
        }
        catch (InstanceValidationException exception)
        {
            return ValidationProblem(context, exception.Code);
        }
    }

    private static IResult ValidationProblem(HttpContext context, string code) =>
        Problem(context, StatusCodes.Status400BadRequest, "The instance request is invalid.", code);

    private static IResult AuthenticationRequired(HttpContext context) =>
        Problem(
            context,
            StatusCodes.Status401Unauthorized,
            "Authentication required.",
            "authentication_required");

    private static IResult AccessDenied(HttpContext context) =>
        Problem(context, StatusCodes.Status403Forbidden, "Access denied.", "access_denied");

    private static IResult InstanceNotFound(HttpContext context) =>
        Problem(context, StatusCodes.Status404NotFound, "Instance not found.", "instance_not_found");

    private static IResult GroupNotFound(HttpContext context) =>
        Problem(
            context,
            StatusCodes.Status404NotFound,
            "Instance group not found.",
            "instance_group_not_found");

    private static IResult InstanceConflict(HttpContext context) =>
        Problem(
            context,
            StatusCodes.Status409Conflict,
            "Instance name or identifier already exists.",
            "instance_conflict");

    private static IResult Problem(
        HttpContext context,
        int statusCode,
        string title,
        string code) =>
        AuthApiProblem.Create(context, statusCode, title, code);
}

public sealed class WriteInstanceRequest
{
    public string? Name { get; init; }

    public string? Kind { get; init; }

    public string? BaseUrl { get; init; }

    public bool Enabled { get; init; } = true;

    public Guid? InstanceGroupId { get; init; }

    public bool TlsVerificationEnabled { get; init; } = true;

    public bool AllowPrivateNetworkAccess { get; init; }
}

public sealed class WriteInstanceGroupRequest
{
    public string? Name { get; init; }
}
