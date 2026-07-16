using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Identity;
using ArrControl.Infrastructure.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ForwardedIpNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace ArrControl.Api.Identity;

public static class LocalAuthApiConstants
{
    public const string AuthenticationScheme = "ArrControlSession";
    public const string AccessCookieName = "__Host-arrcontrol_session";
    public const string RefreshCookieName = "__Host-arrcontrol_refresh";
    public const string CsrfCookieName = "__Host-arrcontrol_csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";
    public const string LoginRateLimitPolicy = "local-login-ip";
    public const string SessionMutationRateLimitPolicy = "local-session-mutation-ip";
}

public static class LocalAuthenticationApi
{
    internal const long LoginRequestSizeLimit = 4 * 1024;

    public static IServiceCollection AddLocalAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = ReadSettings(configuration);
        settings.Validate();
        var transportSettings = ReadTransportSettings(configuration);
        transportSettings.Validate();

        services.AddSingleton(settings);
        services.AddSingleton(transportSettings);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<ISessionTokenService, SecureSessionTokenService>();
        services.AddSingleton<CsrfTokenService>();
        services.AddSingleton<AuthenticationCookieManager>();
        services.AddScoped<IAuthenticationAuditPort, EfAuthenticationAuditPort>();
        services.AddScoped<ILocalIdentityStore, EfLocalIdentityStore>();
        services.AddScoped<LocalIdentityService>();
        services.AddHostedService<LocalAdminBootstrapHostedService>();
        ConfigureTrustedForwardedHeaders(services, configuration);

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = LocalAuthApiConstants.AuthenticationScheme;
                options.DefaultChallengeScheme = LocalAuthApiConstants.AuthenticationScheme;
                options.DefaultForbidScheme = LocalAuthApiConstants.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, LocalSessionAuthenticationHandler>(
                LocalAuthApiConstants.AuthenticationScheme,
                _ => { });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                httpContext =>
                {
                    if (httpContext.Request.Path != OidcAuthenticationApi.CallbackPath
                        && httpContext.Request.Path != OidcAuthenticationApi.SignedOutCallbackPath)
                    {
                        return RateLimitPartition.GetNoLimiter("non-oidc-protocol");
                    }

                    return RateLimitPartition.GetFixedWindowLimiter(
                        "oidc-protocol:"
                        + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            AutoReplenishment = true,
                            PermitLimit = transportSettings.SessionMutationRequestLimit,
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            Window = settings.LoginFailureWindow,
                        });
                });
            options.AddPolicy(
                LocalAuthApiConstants.LoginRateLimitPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = transportSettings.LoginRequestLimit,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        Window = settings.LoginFailureWindow,
                    }));
            options.AddPolicy(
                LocalAuthApiConstants.SessionMutationRateLimitPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = transportSettings.SessionMutationRequestLimit,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        Window = settings.LoginFailureWindow,
                    }));
            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                        .ToString(CultureInfo.InvariantCulture);
                }

                await AuthApiProblem.Create(
                        context.HttpContext,
                        StatusCodes.Status429TooManyRequests,
                        "Too many authentication attempts.",
                        "authentication_rate_limited")
                    .ExecuteAsync(context.HttpContext);
            };
        });

        return services;
    }

    public static IEndpointRouteBuilder MapLocalAuthentication(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth")
            .AllowAnonymous()
            .WithTags("Authentication");

        group.MapGet("/csrf", GetCsrfToken)
            .WithName("getAuthCsrf")
            .WithSummary("Issue a double-submit CSRF token")
            .Produces<CsrfTokenResponse>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/login", LoginAsync)
            .WithName("loginLocal")
            .WithSummary("Create a local cookie session")
            .Accepts<LoginRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .RequireRateLimiting(LocalAuthApiConstants.LoginRateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(LoginRequestSizeLimit))
            .Produces<AuthSessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", RefreshAsync)
            .WithName("refreshLocalSession")
            .WithSummary("Rotate a local refresh token")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .RequireRateLimiting(LocalAuthApiConstants.SessionMutationRateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces<AuthSessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", LogoutAsync)
            .WithName("logoutLocal")
            .WithSummary("Revoke the local session family")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .RequireRateLimiting(LocalAuthApiConstants.SessionMutationRateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private static IResult GetCsrfToken(
        HttpContext context,
        AuthenticationCookieManager cookieManager)
    {
        var token = cookieManager.IssueCsrfCookie(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new CsrfTokenResponse(token));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        LocalIdentityService identityService,
        LocalAuthSettings settings,
        AuthenticationCookieManager cookieManager,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await identityService.LoginAsync(
            request.Email,
            request.Password,
            AuthenticationHttpContext.CreateRequestContext(context),
            cancellationToken);

        if (result.Status == LoginStatus.RateLimited)
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(
                    (result.RetryAfter ?? settings.LoginFailureWindow).TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status429TooManyRequests,
                "Too many authentication attempts.",
                "authentication_rate_limited");
        }

        if (result is not { Status: LoginStatus.Succeeded, Session: not null })
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication failed.",
                "authentication_failed");
        }

        var csrfToken = cookieManager.WriteSessionCookies(context, result.Session);
        return Results.Ok(ToResponse(result.Session, csrfToken));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        LocalIdentityService identityService,
        AuthenticationCookieManager cookieManager,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Request.Cookies.TryGetValue(
            LocalAuthApiConstants.RefreshCookieName,
            out var refreshToken);
        var result = await identityService.RefreshAsync(
            refreshToken,
            AuthenticationHttpContext.CreateRequestContext(context),
            cancellationToken);

        if (result is not { Status: RefreshStatus.Succeeded, Session: not null })
        {
            cookieManager.DeleteSessionCookies(context);
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication failed.",
                "authentication_failed");
        }

        var csrfToken = cookieManager.WriteSessionCookies(context, result.Session);
        return Results.Ok(ToResponse(result.Session, csrfToken));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        LocalIdentityService identityService,
        AuthenticationCookieManager cookieManager,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Request.Cookies.TryGetValue(
            LocalAuthApiConstants.RefreshCookieName,
            out var refreshToken);
        await identityService.LogoutAsync(
            AuthenticationHttpContext.GetSessionId(context),
            refreshToken,
            AuthenticationHttpContext.CreateRequestContext(context),
            cancellationToken);
        cookieManager.DeleteSessionCookies(context);
        return Results.NoContent();
    }

    private static AuthSessionResponse ToResponse(IssuedSession session, string csrfToken) =>
        new(
            session.UserId,
            session.Email,
            session.AccessExpiresAt,
            session.RefreshExpiresAt,
            csrfToken);

    private static LocalAuthSettings ReadSettings(IConfiguration configuration)
    {
        var defaults = LocalAuthSettings.Default;
        return new LocalAuthSettings(
            ReadTimeSpan(
                configuration,
                "Auth:Local:AccessTokenLifetime",
                defaults.AccessTokenLifetime),
            ReadTimeSpan(
                configuration,
                "Auth:Local:RefreshTokenLifetime",
                defaults.RefreshTokenLifetime),
            ReadTimeSpan(
                configuration,
                "Auth:Local:LoginFailureWindow",
                defaults.LoginFailureWindow),
            ReadInt32(
                configuration,
                "Auth:Local:AccountFailureLimit",
                defaults.AccountFailureLimit),
            ReadInt32(
                configuration,
                "Auth:Local:IpFailureLimit",
                defaults.IpFailureLimit));
    }

    private static LocalAuthTransportSettings ReadTransportSettings(IConfiguration configuration)
    {
        var defaults = LocalAuthTransportSettings.Default;
        return new LocalAuthTransportSettings(
            ReadInt32(
                configuration,
                "Auth:Local:LoginRequestLimit",
                defaults.LoginRequestLimit),
            ReadInt32(
                configuration,
                "Auth:Local:SessionMutationRequestLimit",
                defaults.SessionMutationRequestLimit));
    }

    private static void ConfigureTrustedForwardedHeaders(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var knownProxies = configuration
            .GetSection("ReverseProxy:KnownProxies")
            .GetChildren()
            .Select(x => ParseKnownProxy(x.Value))
            .ToArray();
        var knownNetworks = configuration
            .GetSection("ReverseProxy:KnownNetworks")
            .GetChildren()
            .Select(x => ParseKnownNetwork(x.Value))
            .ToArray();
        if (knownProxies.Length == 0 && knownNetworks.Length == 0)
        {
            return;
        }

        var forwardLimit = ReadInt32(configuration, "ReverseProxy:ForwardLimit", 1);
        if (forwardLimit is < 1 or > 10)
        {
            throw new InvalidOperationException(
                "Configuration setting 'ReverseProxy:ForwardLimit' must be between 1 and 10.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = forwardLimit;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in knownProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in knownNetworks)
            {
                options.KnownNetworks.Add(network);
            }
        });
    }

    private static IPAddress ParseKnownProxy(string? value)
    {
        if (value is not null && IPAddress.TryParse(value, out var proxy))
        {
            return proxy;
        }

        throw new InvalidOperationException(
            "A ReverseProxy:KnownProxies entry is not a valid IP address.");
    }

    private static ForwardedIpNetwork ParseKnownNetwork(string? value)
    {
        if (value is not null && ForwardedIpNetwork.TryParse(value, out var network))
        {
            return network;
        }

        throw new InvalidOperationException(
            "A ReverseProxy:KnownNetworks entry is not a valid CIDR network.");
    }

    private static TimeSpan ReadTimeSpan(
        IConfiguration configuration,
        string key,
        TimeSpan fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            return fallback;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new InvalidOperationException($"Configuration setting '{key}' is not a valid duration.");
    }

    private static int ReadInt32(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new InvalidOperationException($"Configuration setting '{key}' is not a valid integer.");
    }
}

public sealed record LocalAuthTransportSettings(
    int LoginRequestLimit,
    int SessionMutationRequestLimit)
{
    public static LocalAuthTransportSettings Default { get; } = new(60, 120);

    public void Validate()
    {
        if (LoginRequestLimit is < 10 or > 10_000)
        {
            throw new InvalidOperationException(
                "The login request limit must be between 10 and 10000.");
        }

        if (SessionMutationRequestLimit is < 10 or > 10_000)
        {
            throw new InvalidOperationException(
                "The session mutation request limit must be between 10 and 10000.");
        }
    }
}

public sealed class LoginRequest
{
    public string? Email { get; init; }

    public string? Password { get; init; }

    public override string ToString() => "[REDACTED]";
}

public sealed record CsrfTokenResponse(string Token);

public sealed record AuthSessionResponse(
    Guid UserId,
    string Email,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt,
    string CsrfToken);

internal static class AuthApiProblem
{
    public static IResult Create(
        HttpContext context,
        int statusCode,
        string title,
        string code)
    {
        context.Response.Headers.CacheControl = "no-store";
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = $"https://arrcontrol.invalid/problems/{code}",
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        return Results.Problem(problem);
    }
}
