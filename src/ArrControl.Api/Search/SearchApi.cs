using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Catalog;
using ArrControl.Application.Connections;
using ArrControl.Application.Operations;
using ArrControl.Application.Providers;
using ArrControl.Application.Search;
using ArrControl.Infrastructure.Search;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Search;

public sealed record SearchRequest(
    string Mode,
    Guid[]? MediaEntityIds,
    Guid[]? InstanceIds,
    Guid[]? InstanceGroupIds,
    bool DryRun = false);

public static class SearchApi
{
    public static IServiceCollection AddSearchOperations(this IServiceCollection services)
    {
        services.AddScoped<ISearchTargetStore, EfSearchTargetStore>();
        services.AddScoped<SearchService>();
        services.AddSingleton<IProviderSearchClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.SonarrClient>());
        services.AddSingleton<IProviderSearchClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.RadarrClient>());
        services.AddSingleton<IProviderSearchClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.LidarrClient>());
        services.AddSingleton<IProviderSearchClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.ReadarrClient>());
        services.AddSingleton<IProviderSearchClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.WhisparrClient>());
        services.AddHostedService<SearchOperationHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapSearch(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/search").WithTags("Search")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.SearchExecute));
        group.MapPost("/preview", PreviewAsync).WithName("previewSearchOperation")
            .Accepts<SearchRequest>("application/json").Produces<SearchScopePreview>();
        group.MapPost("", StartAsync).WithName("startSearchOperation")
            .Accepts<SearchRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(1024 * 1024))
            .Produces<OperationDetails>(202).ProducesProblem(400).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> PreviewAsync(
        SearchRequest request, HttpContext context, SearchService service, CancellationToken token)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
            return Problem(context, 401, "authentication_required");
        try
        {
            var preview = await service.PreviewAsync(
                identity.UserId, identity.SessionId, Scope(request), token);
            return preview is null ? Problem(context, 403, "access_denied") : Results.Ok(preview);
        }
        catch (ArgumentException) { return Problem(context, 400, "search_scope_invalid"); }
    }

    private static async Task<IResult> StartAsync(
        SearchRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext context,
        SearchService service,
        CancellationToken token)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
            return Problem(context, 401, "authentication_required");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return Problem(context, 400, "idempotency_key_required");
        try
        {
            var result = await service.StartAsync(actor, Scope(request), request.DryRun, idempotencyKey, token);
            if (result is null) return Problem(context, 403, "access_denied");
            return result.Status switch
            {
                CreateOperationStatus.Created or CreateOperationStatus.Replayed =>
                    Results.Accepted($"/api/v1/operations/{result.Operation!.Id:D}", result.Operation),
                CreateOperationStatus.IdempotencyConflict => Problem(context, 409, "idempotency_conflict"),
                _ => Problem(context, 400, "search_targets_empty"),
            };
        }
        catch (ArgumentException) { return Problem(context, 400, "search_scope_invalid"); }
    }

    private static SearchScopeRequest Scope(SearchRequest request) => new(
        request.Mode, request.MediaEntityIds ?? [], request.InstanceIds ?? [], request.InstanceGroupIds ?? []);

    private static IResult Problem(HttpContext context, int status, string code) =>
        AuthApiProblem.Create(context, status, "The search request could not be completed.", code);
}

public sealed class SearchOperationHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SearchOperationHostedService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, DateTimeOffset> lastCall = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IOperationStore>();
                foreach (var id in await store.ListPendingAsync("search", 10, stoppingToken))
                    await ExecuteOperationAsync(scope.ServiceProvider, store, id, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Search operation polling failed with error type {ErrorType}.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        }
    }

    private async Task ExecuteOperationAsync(
        IServiceProvider services, IOperationStore store, Guid id, CancellationToken token)
    {
        if (!await store.TryStartAsync(id, token)) return;
        var operation = await store.GetForExecutionAsync(id, token);
        if (operation is null) return;
        if (operation.DryRun)
        {
            foreach (var target in operation.Targets)
                await store.CompleteTargetAsync(id, target.InstanceId, target.TargetKey, true, null,
                    "{\"dryRun\":true}", token);
            await store.CompleteAsync(id, token);
            return;
        }

        var resolver = services.GetRequiredService<ICatalogSyncTargetResolver>();
        var clients = services.GetServices<IProviderSearchClient>().ToArray();
        foreach (var group in operation.Targets.GroupBy(value => value.InstanceId))
        {
            var current = await store.GetForExecutionAsync(id, token);
            if (current?.CancellationRequested == true) break;
            var target = await resolver.ResolveAsync(group.Key, token);
            var client = target is null ? null : clients.SingleOrDefault(value => value.Kind == target.Kind);
            foreach (var chunk in group.Chunk(100))
            {
                if (target is null || client is null)
                {
                    foreach (var item in chunk)
                        await store.CompleteTargetAsync(id, item.InstanceId, item.TargetKey, false,
                            "search_provider_unavailable", null, token);
                    continue;
                }

                if (lastCall.TryGetValue(group.Key, out var previous))
                {
                    var delay = previous.AddSeconds(2) - timeProvider.GetUtcNow();
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, token);
                }
                ProviderCallResult<ProviderSearchResult> result;
                try { result = await client.SearchAsync(target.Connection, chunk.Select(x => x.TargetKey).ToArray(), token); }
                catch (ProviderTransportException exception)
                {
                    result = ProviderCallResult<ProviderSearchResult>.Failed(exception.Code);
                }
                lastCall[group.Key] = timeProvider.GetUtcNow();
                foreach (var item in chunk)
                    await store.CompleteTargetAsync(id, item.InstanceId, item.TargetKey, result.Success,
                        result.ErrorCode,
                        result.Success ? System.Text.Json.JsonSerializer.Serialize(new { result.Value!.CommandId }) : null,
                        token);
                var retry = result.RateLimit?.RetryAfter;
                if (retry > TimeSpan.Zero && retry <= TimeSpan.FromMinutes(1))
                    await Task.Delay(retry.Value, timeProvider, token);
            }
        }
        await store.CompleteAsync(id, token);
    }
}
