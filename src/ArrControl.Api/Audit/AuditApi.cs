using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Audit;
using ArrControl.Application.Authorization;
using ArrControl.Application.Automation;
using ArrControl.Infrastructure.Audit;
using ArrControl.Infrastructure.Automation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Audit;

public sealed class DataProtectionAuditCursorCodec(IDataProtectionProvider provider) : IAuditCursorCodec
{
    private readonly IDataProtector protector = provider.CreateProtector("ArrControl.AuditCursor.v1");

    public string Encode(AuditCursor cursor) => protector.Protect(JsonSerializer.Serialize(cursor));

    public AuditCursor? Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<AuditCursor>(protector.Unprotect(value));
            return cursor is not null && cursor.Id != Guid.Empty
                && cursor.FilterFingerprint.Length == 64
                && cursor.FilterFingerprint.All(char.IsAsciiHexDigit)
                && cursor.Filter.From < cursor.Filter.To
                    ? cursor
                    : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }
}

public static class AuditApi
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IServiceCollection AddAuditOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var defaults = AuditRetentionSettings.Default;
        var settings = new AuditRetentionSettings(
            ReadInt(configuration, "Operations:Audit:RetentionDays", defaults.RetentionDays),
            ReadInt(configuration, "Operations:Audit:RetentionBatchSize", defaults.BatchSize),
            ReadInt(configuration, "Operations:Audit:RetentionMaximumBatches", defaults.MaximumBatches));
        settings.Validate();
        services.AddSingleton(settings);
        services.AddSingleton<IAuditCursorCodec, DataProtectionAuditCursorCodec>();
        services.AddScoped<IAuditQueryStore, EfAuditQueryStore>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<EfAuditMaintenanceStore>();
        services.AddScoped<IAuditRetentionStore>(provider => provider.GetRequiredService<EfAuditMaintenanceStore>());
        services.AddScoped<IDiagnosticsExportStore>(provider => provider.GetRequiredService<EfAuditMaintenanceStore>());
        services.AddScoped<DiagnosticsExportService>();
        services.AddScoped<IAuditRetentionScheduleProvisioner, EfAuditRetentionScheduleProvisioner>();
        services.AddScoped<IScheduledJobHandler, AuditRetentionJobHandler>();
        services.AddHostedService<AuditRetentionScheduleHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAudit(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/audit", QueryAsync)
            .WithName("listAuditEvents").WithTags("Audit")
            .RequireAuthorization(RbacPolicyNames.Global(RbacPermissions.AuditRead))
            .Produces<AuditPage>().ProducesProblem(400).ProducesProblem(403);
        endpoints.MapPost("/api/v1/diagnostics/export", ExportAsync)
            .WithName("exportDiagnostics").WithTags("Diagnostics")
            .RequireAuthorization(RbacPolicyNames.Global(RbacPermissions.AuditRead))
            .Accepts<DiagnosticsExportRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(4096))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(400).ProducesProblem(403);
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(
        HttpContext context,
        AuditQueryService service,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? actorUserId = null,
        string? action = null,
        string? outcome = null,
        string? correlationId = null,
        string? cursor = null,
        int limit = AuditLimits.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
            return Problem(context, 401, "authentication_required");
        var result = await service.QueryAsync(
            identity.UserId, identity.SessionId,
            new AuditFilter(from, to, actorUserId, action, outcome, correlationId),
            cursor, limit, cancellationToken);
        return result.Status switch
        {
            AuditQueryStatus.Success => Results.Ok(result.Page),
            AuditQueryStatus.Forbidden => Problem(context, 403, "access_denied"),
            _ => Problem(context, 400, result.ErrorCode ?? "audit_filter_invalid"),
        };
    }

    private static async Task<IResult> ExportAsync(
        DiagnosticsExportRequest request,
        HttpContext context,
        DiagnosticsExportService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (!RbacHttpContext.TryGetActor(context, out var actor))
            return Problem(context, 401, "authentication_required");
        var result = await service.CreateAsync(actor, request, cancellationToken);
        if (result.Status == DiagnosticsExportStatus.Forbidden)
            return Problem(context, 403, "access_denied");
        if (result.Status == DiagnosticsExportStatus.Invalid || result.Snapshot is null)
            return Problem(context, 400, "diagnostics_export_invalid");

        var bytes = CreateArchive(result.Snapshot);
        var filename = $"arrcontrol-diagnostics-{result.Snapshot.GeneratedAt:yyyyMMdd-HHmmss}.zip";
        return Results.File(bytes, "application/zip", filename, enableRangeProcessing: false);
    }

    private static byte[] CreateArchive(DiagnosticsSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, snapshot, ExportJsonOptions);
        }
        return output.ToArray();
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration[key];
        if (value is null) return fallback;
        return int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration setting '{key}' is not a valid integer.");
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        AuthApiProblem.Create(context, status, "The audit request could not be completed.", code);
}

public sealed class AuditRetentionScheduleHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AuditRetentionScheduleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var changed = await scope.ServiceProvider.GetRequiredService<IAuditRetentionScheduleProvisioner>()
                .ReconcileAsync(token);
            if (changed > 0)
                logger.LogInformation("Audit retention schedule reconciliation changed {ScheduleCount} schedule(s).", changed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Audit retention schedule reconciliation failed with error type {ErrorType}.",
                exception.GetType().Name);
        }
    }
}
