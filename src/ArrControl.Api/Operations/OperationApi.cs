using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Operations;
using ArrControl.Infrastructure.Operations;

namespace ArrControl.Api.Operations;

public static class OperationApi
{
    public static IServiceCollection AddOperationModel(this IServiceCollection services)
    {
        services.AddScoped<IOperationStore, EfOperationStore>();
        services.AddScoped<OperationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/operations/{operationId:guid}", GetAsync)
            .WithName("getOperation").WithTags("Operations").RequireAuthorization()
            .Produces<OperationDetails>().ProducesProblem(404);
        endpoints.MapDelete("/api/v1/operations/{operationId:guid}", CancelAsync)
            .WithName("cancelOperation").WithTags("Operations").RequireAuthorization()
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .Produces<OperationDetails>(202).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid operationId, HttpContext context, OperationService service, CancellationToken token)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
            return AuthApiProblem.Create(context, 401, "Authentication required.", "authentication_required");
        var operation = await service.GetAsync(identity.UserId, operationId, token);
        return operation is null
            ? AuthApiProblem.Create(context, 404, "Operation not found.", "operation_not_found")
            : Results.Ok(operation);
    }

    private static async Task<IResult> CancelAsync(
        Guid operationId, HttpContext context, OperationService service, CancellationToken token)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
            return AuthApiProblem.Create(context, 401, "Authentication required.", "authentication_required");
        if (!await service.CancelAsync(actor, operationId, token))
            return AuthApiProblem.Create(context, 404, "Operation not found.", "operation_not_found");
        return Results.Accepted($"/api/v1/operations/{operationId:D}",
            await service.GetAsync(actor.UserId, operationId, token));
    }
}
