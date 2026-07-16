using System.Security.Claims;
using System.Text.Json;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ArrControl.Api.Identity;

public static class OidcAuthenticationApi
{
    public const string AuthenticationScheme = "ArrControlOidc";
    public const string StagingScheme = "ArrControlOidcStage";
    public const string LoginPath = "/api/v1/auth/oidc/login";
    public const string LogoutPath = "/api/v1/auth/oidc/logout";
    public const string StatusPath = "/api/v1/auth/oidc/status";
    public static readonly PathString CallbackPath = new("/auth/oidc/callback");
    public static readonly PathString SignedOutCallbackPath = new("/auth/oidc/signed-out");

    private const string FailureRedirect = "/login?authError=oidc";
    private const string ValidatedIssuerItem = "ArrControl.Oidc.ValidatedIssuer";
    private const string ValidatedSubjectItem = "ArrControl.Oidc.ValidatedSubject";
    private const string ProtectedIdTokenItem = "ArrControl.Oidc.ProtectedIdToken";

    public static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = OidcProviderSettings.Read(configuration);
        services.AddSingleton(settings);
        services.AddSingleton(new OidcIdentitySettings(settings.RoleMappings));
        services.AddSingleton<OidcLogoutTokenProtector>();
        services.AddScoped<IOidcIdentityStore, EfOidcIdentityStore>();
        services.AddScoped<OidcIdentityService>();

        if (!settings.Enabled)
        {
            return services;
        }

        services.AddAuthentication()
            .AddCookie(StagingScheme, options =>
            {
                options.Cookie.Name = "__Host-arrcontrol_oidc_stage";
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                options.SlidingExpiration = false;
            })
            .AddOpenIdConnect(AuthenticationScheme, options =>
            {
                options.SignInScheme = StagingScheme;
                options.Authority = settings.Authority!.AbsoluteUri;
                options.MetadataAddress = settings.MetadataAddress!.AbsoluteUri;
                options.ClientId = settings.ClientId!;
                options.ClientSecret = settings.ClientSecret!;
                options.RequireHttpsMetadata = true;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = SignedOutCallbackPath;
                options.RemoteSignOutPath = PathString.Empty;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.UseIfAvailable;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.RefreshOnIssuerKeyNotFound = true;
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
                options.BackchannelTimeout = TimeSpan.FromSeconds(30);

                options.Scope.Clear();
                options.Scope.Add(OpenIdConnectScope.OpenId);
                options.Scope.Add(OpenIdConnectScope.Profile);
                options.Scope.Add(OpenIdConnectScope.Email);

                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuer = settings.Authority.AbsoluteUri;
                options.TokenValidationParameters.IssuerValidator =
                    (issuer, _, _) => string.Equals(
                        issuer,
                        settings.Authority.AbsoluteUri,
                        StringComparison.Ordinal)
                        ? issuer
                        : throw new SecurityTokenInvalidIssuerException(
                            "The OIDC token issuer does not match the configured Authority.");
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.RequireExpirationTime = true;
                options.TokenValidationParameters.RequireSignedTokens = true;
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.ValidAlgorithms =
                    [SecurityAlgorithms.RsaSha256];

                ConfigureProtocolCookie(
                    options.CorrelationCookie,
                    "__Host-arrcontrol_oidc_correlation.");
                ConfigureProtocolCookie(
                    options.NonceCookie,
                    "__Host-arrcontrol_oidc_nonce.");

                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = settings.CallbackUri!.AbsoluteUri;
                    context.ProtocolMessage.ResponseType = OpenIdConnectResponseType.Code;
                    context.ProtocolMessage.ResponseMode = OpenIdConnectResponseMode.Query;
                    context.ProtocolMessage.SetParameter(
                        OpenIdConnectParameterNames.ResponseMode,
                        OpenIdConnectResponseMode.Query);
                    return Task.CompletedTask;
                };
                options.Events.OnTokenValidated = ValidateTokenAsync;
                options.Events.OnTicketReceived = CompleteLoginAsync;
                options.Events.OnRemoteFailure = HandleRemoteFailureAsync;
                options.Events.OnAccessDenied = HandleAccessDeniedAsync;
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri =
                        settings.SignedOutCallbackUri!.AbsoluteUri;
                    var idToken = context.Properties.GetTokenValue(
                        OpenIdConnectParameterNames.IdToken);
                    if (!string.IsNullOrEmpty(idToken))
                    {
                        context.ProtocolMessage.IdTokenHint = idToken;
                    }

                    return Task.CompletedTask;
                };
                options.Events.OnSignedOutCallbackRedirect = context =>
                {
                    var returnUrl = OidcReturnUrl.GetSafeOrDefault(
                        context.Properties?.RedirectUri);
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Redirect(returnUrl);
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    public static IEndpointRouteBuilder MapOidcAuthentication(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth/oidc")
            .AllowAnonymous()
            .WithTags("Authentication");

        group.MapGet("/status", (OidcProviderSettings settings) =>
                Results.Ok(new OidcStatusResponse(
                    settings.Enabled,
                    settings.Enabled ? LoginPath : null)))
            .WithName("getOidcStatus")
            .WithSummary("Get OIDC availability")
            .Produces<OidcStatusResponse>();

        group.MapGet("/login", StartLogin)
            .WithName("loginOidc")
            .WithSummary("Start OIDC Authorization Code with PKCE")
            .RequireRateLimiting(LocalAuthApiConstants.LoginRateLimitPolicy)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", LogoutAsync)
            .WithName("logoutOidc")
            .WithSummary("Revoke the local session and start OIDC RP logout")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .RequireRateLimiting(LocalAuthApiConstants.SessionMutationRateLimitPolicy)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        endpoints.MapGet(CallbackPath, DisabledProtocolEndpoint)
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(SignedOutCallbackPath, DisabledProtocolEndpoint)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }

    private static IResult StartLogin(
        HttpContext context,
        OidcProviderSettings settings,
        string? returnUrl)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!settings.Enabled)
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status404NotFound,
                "OIDC authentication is not enabled.",
                "oidc_not_enabled");
        }

        if (!OidcReturnUrl.TryGetSafe(returnUrl, out var safeReturnUrl))
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status400BadRequest,
                "The return URL is invalid.",
                "invalid_return_url");
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = safeReturnUrl },
            [AuthenticationScheme]);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        OidcProviderSettings settings,
        OidcIdentityService oidcIdentityService,
        LocalIdentityService localIdentityService,
        AuthenticationCookieManager cookieManager,
        OidcLogoutTokenProtector tokenProtector,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!settings.Enabled)
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status404NotFound,
                "OIDC authentication is not enabled.",
                "oidc_not_enabled");
        }

        if (!OidcReturnUrl.TryGetSafe(returnUrl, out var safeReturnUrl))
        {
            return AuthApiProblem.Create(
                context,
                StatusCodes.Status400BadRequest,
                "The return URL is invalid.",
                "invalid_return_url");
        }

        context.Request.Cookies.TryGetValue(
            LocalAuthApiConstants.RefreshCookieName,
            out var refreshToken);
        var sessionId = AuthenticationHttpContext.GetSessionId(context);
        var logoutContext = await oidcIdentityService.GetLogoutContextAsync(
            sessionId,
            refreshToken,
            cancellationToken);

        await localIdentityService.LogoutAsync(
            sessionId,
            refreshToken,
            AuthenticationHttpContext.CreateRequestContext(context),
            cancellationToken);
        cookieManager.DeleteSessionCookies(context);

        if (logoutContext is null
            || !tokenProtector.TryUnprotect(logoutContext.ProtectedIdToken, out var idToken))
        {
            return Results.Redirect(safeReturnUrl);
        }

        var properties = new AuthenticationProperties { RedirectUri = safeReturnUrl };
        properties.StoreTokens(
        [
            new AuthenticationToken
            {
                Name = OpenIdConnectParameterNames.IdToken,
                Value = idToken,
            },
        ]);
        return Results.SignOut(properties, [AuthenticationScheme]);
    }

    private static IResult DisabledProtocolEndpoint(
        HttpContext context,
        OidcProviderSettings settings) =>
        settings.Enabled
            ? AuthApiProblem.Create(
                context,
                StatusCodes.Status400BadRequest,
                "The OIDC protocol response is invalid.",
                "oidc_protocol_failed")
            : AuthApiProblem.Create(
                context,
                StatusCodes.Status404NotFound,
                "OIDC authentication is not enabled.",
                "oidc_not_enabled");

    private static Task ValidateTokenAsync(TokenValidatedContext context)
    {
        var subjects = context.Principal?.FindAll("sub").ToArray()
            ?? Array.Empty<Claim>();
        var issuer = context.SecurityToken.Issuer;
        var idToken = context.TokenEndpointResponse?.IdToken;
        var protector = context.HttpContext.RequestServices
            .GetRequiredService<OidcLogoutTokenProtector>();
        if (subjects.Length != 1
            || subjects[0].ValueType != ClaimValueTypes.String
            || string.IsNullOrWhiteSpace(subjects[0].Value)
            || !TryReadRawStringSubject(idToken, out var rawSubject)
            || !string.Equals(subjects[0].Value, rawSubject, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(issuer)
            || !protector.TryProtect(idToken, out var protectedIdToken))
        {
            context.Fail("The OIDC identity response is invalid.");
            return Task.CompletedTask;
        }

        context.HttpContext.Items[ValidatedIssuerItem] = issuer;
        context.HttpContext.Items[ValidatedSubjectItem] = rawSubject;
        context.HttpContext.Items[ProtectedIdTokenItem] = protectedIdToken;
        return Task.CompletedTask;
    }

    private static bool TryReadRawStringSubject(string? idToken, out string subject)
    {
        subject = string.Empty;
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return false;
        }

        try
        {
            var segments = idToken.Split('.');
            if (segments.Length != 3)
            {
                return false;
            }

            using var document = JsonDocument.Parse(
                Base64UrlEncoder.DecodeBytes(segments[1]));
            var subjectProperties = document.RootElement
                .EnumerateObject()
                .Where(property => string.Equals(
                    property.Name,
                    JwtRegisteredClaimNames.Sub,
                    StringComparison.Ordinal))
                .ToArray();
            if (subjectProperties.Length != 1
                || subjectProperties[0].Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            subject = subjectProperties[0].Value.GetString() ?? string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FormatException
                or InvalidOperationException
                or JsonException)
        {
            return false;
        }
    }

    private static async Task CompleteLoginAsync(TicketReceivedContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (context.Principal is null
                || context.HttpContext.Items[ValidatedIssuerItem] is not string issuer
                || context.HttpContext.Items[ValidatedSubjectItem] is not string subject
                || context.HttpContext.Items[ProtectedIdTokenItem] is not string protectedIdToken
                || !TryReadOptionalSingleClaim(context.Principal, "email", out var email)
                || !TryReadEmailVerified(context.Principal, out var emailVerified)
                || !TryReadGroups(context.Principal, out var groups))
            {
                await RecordProtocolFailureAsync(context.HttpContext);
                RedirectFailedLogin(context);
                return;
            }

            var service = context.HttpContext.RequestServices
                .GetRequiredService<OidcIdentityService>();
            var result = await service.LoginAsync(
                new OidcIdentityClaims(
                    issuer,
                    subject,
                    email,
                    emailVerified,
                    groups),
                protectedIdToken,
                AuthenticationHttpContext.CreateRequestContext(context.HttpContext),
                context.HttpContext.RequestAborted);
            if (result is not { Status: OidcLoginStatus.Succeeded, Session: not null })
            {
                RedirectFailedLogin(context);
                return;
            }

            var cookieManager = context.HttpContext.RequestServices
                .GetRequiredService<AuthenticationCookieManager>();
            cookieManager.WriteSessionCookies(context.HttpContext, result.Session);
            context.Response.Redirect(
                OidcReturnUrl.GetSafeOrDefault(context.ReturnUri));
            context.HandleResponse();
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityValidationException)
        {
            await RecordProtocolFailureAsync(context.HttpContext);
            RedirectFailedLogin(context);
        }
    }

    private static async Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        await RecordProtocolFailureAsync(context.HttpContext);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(FailureRedirect);
        context.HandleResponse();
    }

    private static async Task HandleAccessDeniedAsync(AccessDeniedContext context)
    {
        await RecordProtocolFailureAsync(context.HttpContext);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(FailureRedirect);
        context.HandleResponse();
    }

    private static Task RecordProtocolFailureAsync(HttpContext context) =>
        context.RequestServices.GetRequiredService<OidcIdentityService>()
            .RecordProtocolFailureAsync(
                AuthenticationHttpContext.CreateRequestContext(context),
                context.RequestAborted);

    private static void RedirectFailedLogin(TicketReceivedContext context)
    {
        context.Response.Redirect(FailureRedirect);
        context.HandleResponse();
    }

    private static bool TryReadEmailVerified(
        ClaimsPrincipal principal,
        out bool emailVerified)
    {
        emailVerified = false;
        var claims = principal.FindAll("email_verified").ToArray();
        if (claims.Length == 0)
        {
            return true;
        }

        return claims.Length == 1
            && claims[0].ValueType == ClaimValueTypes.Boolean
            && bool.TryParse(claims[0].Value, out emailVerified);
    }

    private static bool TryReadOptionalSingleClaim(
        ClaimsPrincipal principal,
        string claimType,
        out string? value)
    {
        var claims = principal.FindAll(claimType).ToArray();
        value = claims.Length == 1 ? claims[0].Value : null;
        return claims.Length <= 1;
    }

    private static bool TryReadGroups(
        ClaimsPrincipal principal,
        out IReadOnlyCollection<string> groups)
    {
        var values = new List<string>();
        foreach (var claim in principal.FindAll("groups"))
        {
            if (claim.ValueType != JsonClaimValueTypes.JsonArray)
            {
                if (claim.ValueType != ClaimValueTypes.String)
                {
                    groups = Array.Empty<string>();
                    return false;
                }

                values.Add(claim.Value);
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    groups = Array.Empty<string>();
                    return false;
                }

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        groups = Array.Empty<string>();
                        return false;
                    }

                    values.Add(item.GetString()!);
                }
            }
            catch (JsonException)
            {
                groups = Array.Empty<string>();
                return false;
            }
        }

        groups = values;
        return true;
    }

    private static void ConfigureProtocolCookie(CookieBuilder cookie, string name)
    {
        cookie.Name = name;
        cookie.Path = "/";
        cookie.HttpOnly = true;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.SameSite = SameSiteMode.None;
        cookie.IsEssential = true;
    }
}

public sealed record OidcStatusResponse(bool Enabled, string? LoginUrl);

internal static class OidcReturnUrl
{
    private const int MaximumLength = 2048;

    public static bool TryGetSafe(string? value, out string safeReturnUrl)
    {
        if (string.IsNullOrEmpty(value))
        {
            safeReturnUrl = "/";
            return true;
        }

        if (value.Length > MaximumLength
            || value[0] != '/'
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("/\\", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Any(char.IsControl))
        {
            safeReturnUrl = string.Empty;
            return false;
        }

        safeReturnUrl = value;
        return true;
    }

    public static string GetSafeOrDefault(string? value) =>
        TryGetSafe(value, out var safeReturnUrl) ? safeReturnUrl : "/";
}
