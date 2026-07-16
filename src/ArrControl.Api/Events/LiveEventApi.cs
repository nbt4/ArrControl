using System.Text.Json;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Events;
using ArrControl.Infrastructure.Events;
using Microsoft.AspNetCore.Http.Features;

namespace ArrControl.Api.Events;

public static class LiveEventApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddLiveEvents(this IServiceCollection services)
    {
        services.AddScoped<ILiveEventStore, EfLiveEventStore>();
        services.AddScoped<LiveEventService>();
        services.AddScoped<IOutboxPublisher, EfOutboxPublisher>();
        services.AddHostedService<OutboxPublisherHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapLiveEvents(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/events/snapshot", SnapshotAsync)
            .WithName("getLiveEventSnapshot").WithTags("Events").RequireAuthorization()
            .Produces<LiveSnapshot>().ProducesProblem(StatusCodes.Status403Forbidden);
        endpoints.MapGet("/api/v1/events", StreamAsync)
            .WithName("streamLiveEvents").WithTags("Events").RequireAuthorization()
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    private static async Task<IResult> SnapshotAsync(
        HttpContext context,
        LiveEventService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
            return AuthApiProblem.Create(context, 401, "Authentication required.", "authentication_required");
        var snapshot = await service.GetSnapshotAsync(
            identity.UserId, identity.SessionId, cancellationToken);
        return snapshot is null
            ? AuthApiProblem.Create(context, 403, "Access denied.", "access_denied")
            : Results.Ok(snapshot);
    }

    private static async Task StreamAsync(
        HttpContext context,
        LiveEventService service,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var lastEventId = context.Request.Headers["Last-Event-ID"].FirstOrDefault();
        var session = await service.OpenAsync(
            identity.UserId,
            identity.SessionId,
            string.IsNullOrWhiteSpace(lastEventId) ? cursor : lastEventId,
            cancellationToken);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "private, no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        if (session.Status == LiveEventSessionStatus.SnapshotRequired)
        {
            var snapshot = await service.GetSnapshotAsync(
                identity.UserId, identity.SessionId, cancellationToken);
            await WriteAsync(context.Response, null, "snapshot-required", snapshot, cancellationToken);
            return;
        }

        var heartbeatAt = DateTimeOffset.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = await service.ReadAsync(session, 200, cancellationToken);
                foreach (var liveEvent in batch.Events)
                    await WriteAsync(context.Response, liveEvent.Id.ToString("D"), "changed", liveEvent,
                        cancellationToken);
                if (batch.CursorAdvanced)
                {
                    await WriteAsync(context.Response, batch.Cursor, "cursor", new { version = 1 }, cancellationToken);
                    session = service.Advance(session, batch);
                }

                await context.Response.Body.FlushAsync(cancellationToken);
                if (batch.HasMore) continue;
                if (DateTimeOffset.UtcNow - heartbeatAt >= TimeSpan.FromSeconds(15))
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    heartbeatAt = DateTimeOffset.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static Task WriteAsync(
        HttpResponse response,
        string? id,
        string eventName,
        object? data,
        CancellationToken cancellationToken)
    {
        var prefix = id is null ? string.Empty : $"id: {id}\n";
        return response.WriteAsync(
            $"{prefix}event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonOptions)}\n\n",
            cancellationToken);
    }
}

public sealed class OutboxPublisherHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
                var published = await publisher.PublishBatchAsync(200, stoppingToken);
                var now = timeProvider.GetUtcNow();
                if (now >= nextCleanup)
                {
                    await publisher.DeleteExpiredAsync(now.AddDays(-7), 10_000, stoppingToken);
                    nextCleanup = now.AddHours(1);
                }
                if (published == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Outbox publication failed with error type {ErrorType}.",
                    exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        }
    }
}
