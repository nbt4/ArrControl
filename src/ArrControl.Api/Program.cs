using ArrControl.Api.Activity;
using ArrControl.Api.Audit;
using ArrControl.Api.Automation;
using ArrControl.Api.Authorization;
using ArrControl.Api.Catalog;
using ArrControl.Api.Connections;
using ArrControl.Api.Events;
using ArrControl.Api.Health;
using ArrControl.Api.Identity;
using ArrControl.Api.Operations;
using ArrControl.Api.Search;
using ArrControl.Infrastructure.Operations;
using ArrControl.Infrastructure.Events;
using ArrControl.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

var migrationRequested = DatabaseMigrationCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(args);
if (migrationRequested)
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
}

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("ArrControl");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    if (!Path.IsPathFullyQualified(dataProtectionKeysPath))
    {
        throw new InvalidOperationException(
            "Configuration setting 'DataProtection:KeysPath' must be an absolute path.");
    }

    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var statusCode = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        var defaultCode = statusCode switch
        {
            StatusCodes.Status400BadRequest => "invalid_request",
            StatusCodes.Status404NotFound => "not_found",
            StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
            StatusCodes.Status413PayloadTooLarge => "request_too_large",
            StatusCodes.Status429TooManyRequests => "request_rate_limited",
            StatusCodes.Status500InternalServerError => "internal_error",
            _ => "http_error",
        };
        context.ProblemDetails.Extensions.TryAdd("code", defaultCode);
        context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
        var code = context.ProblemDetails.Extensions["code"] as string ?? defaultCode;
        context.ProblemDetails.Type ??= $"https://arrcontrol.invalid/problems/{code}";
        context.ProblemDetails.Title ??= ReasonPhrases.GetReasonPhrase(statusCode);
    };
});
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(180);
    options.IncludeSubDomains = false;
    options.Preload = false;
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<LocalAuthOpenApiDocumentTransformer>();
    options.AddSchemaTransformer<LocalAuthOpenApiSchemaTransformer>();
});
builder.Services.AddSingleton<TransactionalOutboxInterceptor>();
builder.Services.AddDbContextFactory<ArrControlDbContext>((services, options) => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Database"))
    .AddInterceptors(services.GetRequiredService<TransactionalOutboxInterceptor>()));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ArrControlDbContext>()
    .AddCheck<PendingMigrationsHealthCheck>("database_migrations");
builder.Services.AddLocalAuthentication(builder.Configuration);
builder.Services.AddOidcAuthentication(builder.Configuration);
builder.Services.AddRbacAuthorization();
builder.Services.AddCredentialProtection(builder.Configuration);
builder.Services.AddInstanceManagement();
builder.Services.AddCatalogSynchronization();
builder.Services.AddActivitySynchronization();
builder.Services.AddOperationModel();
builder.Services.AddSearchOperations();
builder.Services.AddHealthIncidents();
builder.Services.AddLiveEvents();
builder.Services.AddAuditOperations(builder.Configuration);
builder.Services.AddScoped<DatabaseMigrationRunner>();
builder.Services.AddAutomationScheduler(builder.Configuration);

var app = builder.Build();
if (migrationRequested)
{
    Environment.ExitCode = await DatabaseMigrationCommand.RunAsync(
        app.Services,
        app.Logger,
        app.Lifetime.ApplicationStopping);
    return;
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "DENY");
        headers.TryAdd("Referrer-Policy", "no-referrer");
        headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
        headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin");
        headers.TryAdd("Cross-Origin-Resource-Policy", "same-origin");
        headers.TryAdd("Content-Security-Policy", context.Request.IsHttps
            ? "default-src 'self'; base-uri 'self'; connect-src 'self'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; img-src 'self' data:; object-src 'none'; script-src 'self'; style-src 'self'; upgrade-insecure-requests"
            : "default-src 'self'; base-uri 'self'; connect-src 'self'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; img-src 'self' data:; object-src 'none'; script-src 'self'; style-src 'self'");
        return Task.CompletedTask;
    });
    await next(context);
});
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi("/api/openapi/{documentName}.json").AllowAnonymous();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path == "/api/v1/auth/login"
        && context.Request.ContentLength > LocalAuthenticationApi.LoginRequestSizeLimit)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapLocalAuthentication();
app.MapOidcAuthentication();
app.MapRbacAuthorization();
app.MapGet("/api/v1/system/status", () => Results.Ok(new { name = "ArrControl", status = "ok", version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev", utc = DateTimeOffset.UtcNow })).AllowAnonymous();
app.MapInstanceReads();
app.MapCredentials();
app.MapMissing();
app.MapActivity();
app.MapOperations();
app.MapSearch();
app.MapHealthIncidents();
app.MapLiveEvents();
app.MapAudit();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();
app.Run();

public partial class Program;
