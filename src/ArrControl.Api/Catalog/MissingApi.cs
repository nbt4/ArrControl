using System.Security.Cryptography;
using System.Text.Json;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Catalog;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Catalog;

public sealed class DataProtectionMissingCursorCodec(IDataProtectionProvider provider)
    : IMissingCursorCodec
{
    private readonly IDataProtector protector = provider.CreateProtector("ArrControl.MissingCursor.v1");

    public string Encode(MissingCursor cursor) =>
        protector.Protect(JsonSerializer.Serialize(cursor));

    public MissingCursor? Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            return null;
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<MissingCursor>(protector.Unprotect(value));
            return cursor is not null
                && cursor.FilterFingerprint.Length == 64
                && cursor.FilterFingerprint.All(character => char.IsAsciiHexDigit(character))
                && cursor.SortTitle.Length <= 1000
                && cursor.MediaEntityId != Guid.Empty
                    ? cursor
                    : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }
}

public sealed record WriteMissingSavedViewRequest(
    string Name,
    Guid[]? InstanceIds,
    string[]? Kinds,
    string[]? Reasons,
    string? Search);

public static class MissingApi
{
    public static IEndpointRouteBuilder MapMissing(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/missing")
            .WithTags("Missing")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.LibraryRead));
        group.MapGet("", QueryAsync)
            .WithName("listMissing")
            .Produces<MissingPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/views", ListViewsAsync)
            .WithName("listMissingSavedViews")
            .Produces<MissingSavedView[]>();
        group.MapPut("/views/{viewId:guid}", UpsertViewAsync)
            .WithName("upsertMissingSavedView")
            .Accepts<WriteMissingSavedViewRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .Produces<MissingSavedView>(StatusCodes.Status200OK)
            .Produces<MissingSavedView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/views/{viewId:guid}", DeleteViewAsync)
            .WithName("deleteMissingSavedView")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(
        HttpContext context,
        MissingQueryService service,
        string? cursor = null,
        int limit = 50,
        Guid? savedViewId = null,
        [FromQuery(Name = "instanceId")] Guid[]? instanceIds = null,
        [FromQuery(Name = "kind")] string[]? kinds = null,
        [FromQuery(Name = "reason")] string[]? reasons = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "private, no-cache";
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        }

        var filter = new MissingFilter(
            instanceIds ?? [],
            kinds ?? [],
            reasons is { Length: > 0 } ? reasons : [MissingReasons.Missing],
            search);
        var result = await service.QueryAsync(
            identity.UserId,
            identity.SessionId,
            filter,
            savedViewId,
            cursor,
            limit,
            cancellationToken);
        return result.Status switch
        {
            MissingQueryStatus.Success => Results.Ok(result.Page),
            MissingQueryStatus.Forbidden => Problem(context, StatusCodes.Status403Forbidden, "access_denied"),
            MissingQueryStatus.SavedViewNotFound => Problem(context, StatusCodes.Status404NotFound, "missing_view_not_found"),
            _ => Problem(context, StatusCodes.Status400BadRequest, result.ErrorCode ?? "missing_filter_invalid"),
        };
    }

    private static async Task<IResult> ListViewsAsync(
        HttpContext context,
        MissingSavedViewService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        }

        return Results.Ok(await service.ListAsync(identity.UserId, cancellationToken));
    }

    private static async Task<IResult> UpsertViewAsync(
        Guid viewId,
        WriteMissingSavedViewRequest request,
        HttpContext context,
        MissingSavedViewService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        }

        var result = await service.UpsertAsync(
            actor,
            viewId,
            request.Name,
            new MissingFilter(
                request.InstanceIds ?? [],
                request.Kinds ?? [],
                request.Reasons is { Length: > 0 } ? request.Reasons : [MissingReasons.Missing],
                request.Search),
            cancellationToken);
        return result.Status switch
        {
            MissingSavedViewWriteStatus.Created => Results.Created(
                $"/api/v1/missing/views/{viewId:D}", result.View),
            MissingSavedViewWriteStatus.Updated => Results.Ok(result.View),
            MissingSavedViewWriteStatus.NameConflict => Problem(
                context,
                StatusCodes.Status409Conflict,
                "missing_view_name_conflict"),
            _ => Problem(context, StatusCodes.Status400BadRequest, "missing_view_invalid"),
        };
    }

    private static async Task<IResult> DeleteViewAsync(
        Guid viewId,
        HttpContext context,
        MissingSavedViewService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        }

        await service.DeleteAsync(actor, viewId, cancellationToken);
        return Results.NoContent();
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        AuthApiProblem.Create(
            context,
            status,
            status == StatusCodes.Status400BadRequest
                ? "The missing query is invalid."
                : "The request could not be completed.",
            code);
}
