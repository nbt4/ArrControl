using System.Security.Claims;
using System.Text.Encodings.Web;
using ArrControl.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ArrControl.Api.Identity;

public sealed class LocalSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalIdentityService identityService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(LocalAuthApiConstants.AccessCookieName, out var accessToken))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await identityService.ValidateAccessTokenAsync(
            accessToken,
            Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("The session is invalid or expired.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim(ClaimTypes.Email, session.Email),
            new Claim(LocalIdentityConstants.SessionIdClaim, session.SessionId.ToString()),
            new Claim(
                LocalIdentityConstants.AuthenticationMethodClaim,
                session.AuthenticationMethod),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            StatusCodes.Status401Unauthorized,
            "Authentication required.",
            "authentication_required");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            StatusCodes.Status403Forbidden,
            "Access denied.",
            "access_denied");

    private async Task WriteProblemAsync(int statusCode, string title, string code)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/problem+json";
        Response.Headers.CacheControl = "no-store";
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = $"https://arrcontrol.invalid/problems/{code}",
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Context.TraceIdentifier;
        await Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: Context.RequestAborted);
    }
}
