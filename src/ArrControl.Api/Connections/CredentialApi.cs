using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Connections;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Connections;

public static class CredentialApi
{
    private const long CredentialRequestSizeLimit = 8 * 1024;

    public static IServiceCollection AddCredentialProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(serviceProvider => ReadKeyRing(
            serviceProvider.GetRequiredService<IConfiguration>()));
        services.AddHostedService<CredentialEncryptionStartupValidator>();
        services.AddSingleton<ICredentialProtector, AesGcmCredentialProtector>();
        services.AddScoped<ICredentialStore, EfCredentialStore>();
        services.AddScoped<CredentialService>();
        return services;
    }

    public static IEndpointRouteBuilder MapCredentials(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/instances/{instanceId:guid}/credentials",
                ListMetadataAsync)
            .WithName("listInstanceCredentials")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesRead))
            .Produces<CredentialMetadata[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPut(
                "/api/v1/instances/{instanceId:guid}/credentials/{purpose}",
                PutAsync)
            .WithName("putInstanceCredential")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesManage))
            .Accepts<WriteCredentialRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(CredentialRequestSizeLimit))
            .Produces<CredentialMetadata>(StatusCodes.Status200OK)
            .Produces<CredentialMetadata>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapDelete(
                "/api/v1/instances/{instanceId:guid}/credentials/{purpose}",
                DeleteAsync)
            .WithName("deleteInstanceCredential")
            .WithTags("Instances")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesManage))
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> ListMetadataAsync(
        Guid instanceId,
        HttpContext context,
        CredentialService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            return Problem(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                "authentication_required");
        }

        try
        {
            var metadata = await service.ListMetadataAsync(
                identity.UserId,
                identity.SessionId,
                instanceId,
                cancellationToken);
            return metadata is null
                ? InstanceNotFound(context)
                : Results.Ok(metadata);
        }
        catch (CredentialValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static async Task<IResult> PutAsync(
        Guid instanceId,
        string purpose,
        WriteCredentialRequest request,
        HttpContext context,
        CredentialService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var result = await service.PutAsync(
                actor,
                instanceId,
                purpose,
                request.Secret,
                cancellationToken);
            return result.Status switch
            {
                PutCredentialStatus.Created => Results.Created(
                    $"/api/v1/instances/{instanceId}/credentials",
                    result.Credential),
                PutCredentialStatus.Updated => Results.Ok(result.Credential),
                PutCredentialStatus.NotFound => InstanceNotFound(context),
                PutCredentialStatus.EncryptionUnavailable => Problem(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Credential encryption is unavailable.",
                    "credential_encryption_unavailable"),
                _ => throw new InvalidOperationException("The credential write result is invalid."),
            };
        }
        catch (CredentialValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid instanceId,
        string purpose,
        HttpContext context,
        CredentialService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return AccessDenied(context);
        }

        try
        {
            var status = await service.DeleteAsync(
                actor,
                instanceId,
                purpose,
                cancellationToken);
            return status switch
            {
                DeleteCredentialStatus.Deleted
                    or DeleteCredentialStatus.Absent => Results.NoContent(),
                DeleteCredentialStatus.NotFound => InstanceNotFound(context),
                _ => throw new InvalidOperationException("The credential deletion result is invalid."),
            };
        }
        catch (CredentialValidationException exception)
        {
            return ValidationProblem(context, exception);
        }
    }

    private static CredentialEncryptionKeyRing ReadKeyRing(IConfiguration configuration)
    {
        var activeVersionValue = configuration["CredentialEncryption:ActiveKeyVersion"];
        var keySections = configuration.GetSection("CredentialEncryption:Keys")
            .GetChildren()
            .ToArray();
        if (activeVersionValue is null && keySections.Length == 0)
        {
            return CredentialEncryptionKeyRing.Empty;
        }

        if (!int.TryParse(activeVersionValue, out var activeVersion)
            || activeVersion <= 0
            || keySections.Length == 0)
        {
            throw new InvalidOperationException(
                "Credential master-key configuration is invalid.");
        }

        var keyFiles = new List<CredentialKeyFile>(keySections.Length);
        foreach (var keySection in keySections)
        {
            if (!int.TryParse(keySection["Version"], out var version)
                || version <= 0
                || string.IsNullOrWhiteSpace(keySection["Path"]))
            {
                throw new InvalidOperationException(
                    "Credential master-key configuration is invalid.");
            }

            keyFiles.Add(new CredentialKeyFile(version, keySection["Path"]!));
        }

        return CredentialEncryptionKeyRing.Load(activeVersion, keyFiles);
    }

    private static IResult ValidationProblem(
        HttpContext context,
        CredentialValidationException exception) =>
        Problem(
            context,
            StatusCodes.Status400BadRequest,
            "The credential request is invalid.",
            exception.Code);

    private static IResult InstanceNotFound(HttpContext context) =>
        Problem(
            context,
            StatusCodes.Status404NotFound,
            "Instance not found.",
            "instance_not_found");

    private static IResult AccessDenied(HttpContext context) =>
        Problem(context, StatusCodes.Status403Forbidden, "Access denied.", "access_denied");

    private static IResult Problem(
        HttpContext context,
        int statusCode,
        string title,
        string code) =>
        AuthApiProblem.Create(context, statusCode, title, code);
}

public sealed class WriteCredentialRequest
{
    public string? Secret { get; init; }

    public override string ToString() => "[REDACTED]";
}

public sealed class CredentialEncryptionStartupValidator(
    CredentialEncryptionKeyRing keyRing) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = keyRing.IsConfigured;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
