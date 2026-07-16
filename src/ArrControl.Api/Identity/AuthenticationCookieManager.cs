using System.Security.Claims;
using ArrControl.Application.Identity;

namespace ArrControl.Api.Identity;

public sealed class AuthenticationCookieManager(CsrfTokenService csrfTokenService)
{
    public string IssueCsrfCookie(HttpContext context)
    {
        var token = csrfTokenService.Issue();
        context.Response.Cookies.Append(
            LocalAuthApiConstants.CsrfCookieName,
            token,
            CreateCookieOptions(httpOnly: false));
        return token;
    }

    public string WriteSessionCookies(HttpContext context, IssuedSession session)
    {
        context.Response.Cookies.Append(
            LocalAuthApiConstants.AccessCookieName,
            session.AccessToken.Value,
            CreateCookieOptions(httpOnly: true, session.AccessExpiresAt));
        context.Response.Cookies.Append(
            LocalAuthApiConstants.RefreshCookieName,
            session.RefreshToken.Value,
            CreateCookieOptions(httpOnly: true, session.RefreshExpiresAt));
        return IssueCsrfCookie(context);
    }

    public void DeleteSessionCookies(HttpContext context)
    {
        context.Response.Cookies.Delete(
            LocalAuthApiConstants.AccessCookieName,
            CreateCookieOptions(httpOnly: true));
        context.Response.Cookies.Delete(
            LocalAuthApiConstants.RefreshCookieName,
            CreateCookieOptions(httpOnly: true));
        context.Response.Cookies.Delete(
            LocalAuthApiConstants.CsrfCookieName,
            CreateCookieOptions(httpOnly: false));
    }

    private static CookieOptions CreateCookieOptions(
        bool httpOnly,
        DateTimeOffset? expires = null) =>
        new()
        {
            Path = "/",
            HttpOnly = httpOnly,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = expires,
        };
}

internal static class AuthenticationHttpContext
{
    public static AuthenticationRequestContext CreateRequestContext(HttpContext context)
    {
        const int maximumCorrelationIdLength = 128;
        var correlationId = context.TraceIdentifier;
        if (correlationId.Length > maximumCorrelationIdLength)
        {
            correlationId = correlationId[..maximumCorrelationIdLength];
        }

        return new AuthenticationRequestContext(
            correlationId,
            context.Connection.RemoteIpAddress);
    }

    public static Guid? GetSessionId(HttpContext context)
    {
        var value = context.User.FindFirstValue(LocalIdentityConstants.SessionIdClaim);
        return Guid.TryParse(value, out var sessionId) ? sessionId : null;
    }
}
