using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;
using Xunit;

namespace ArrControl.AuthentikIntegrationTests;

[Collection(AuthentikContainerCollection.Name)]
public sealed class AuthorizationCodePkceTests(AuthentikContainerFixture authentik)
{
    [AuthentikBrowserFact]
    public async Task Authorization_code_requires_pkce_and_returns_a_valid_oidc_identity()
    {
        using var playwright = await CreatePlaywrightAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var browserDiagnostics = new ConcurrentQueue<string>();
        AttachBrowserDiagnostics(page, browserDiagnostics);

        var rejectedAuthorization = await AuthorizeAsync(
            page,
            loginRequired: true,
            browserDiagnostics);
        using var rejectedExchange = await ExchangeCodeAsync(
            rejectedAuthorization.Code,
            GenerateSecret(48));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedExchange.StatusCode);
        using (var problem = JsonDocument.Parse(
                   await rejectedExchange.Content.ReadAsStringAsync(CancellationToken.None)))
        {
            Assert.Equal("invalid_grant", problem.RootElement.GetProperty("error").GetString());
        }

        var authorization = await AuthorizeAsync(
            page,
            loginRequired: false,
            browserDiagnostics);
        using var exchange = await ExchangeCodeAsync(authorization.Code, authorization.Verifier);
        exchange.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(
            await exchange.Content.ReadAsStringAsync(CancellationToken.None));
        var token = tokenDocument.RootElement;

        Assert.Equal("Bearer", token.GetProperty("token_type").GetString(), ignoreCase: true);
        Assert.False(token.TryGetProperty("refresh_token", out _));
        var accessToken = token.GetProperty("access_token").GetString();
        var idToken = token.GetProperty("id_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.False(string.IsNullOrWhiteSpace(idToken));

        var principal = await ValidateIdTokenAsync(idToken!);
        Assert.Equal(authentik.Authority.AbsoluteUri, ClaimValue(principal, "iss"));
        Assert.False(string.IsNullOrWhiteSpace(ClaimValue(principal, "sub")));
        Assert.Equal(authentik.UserEmail, ClaimValue(principal, "email"));
        Assert.Equal("true", ClaimValue(principal, "email_verified"), ignoreCase: true);
        Assert.Equal(authorization.Nonce, ClaimValue(principal, "nonce"));

        using var userInfoRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(authentik.BaseAddress, "/application/o/userinfo/"));
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var client = authentik.CreateAnonymousClient();
        using var userInfoResponse = await client.SendAsync(userInfoRequest, CancellationToken.None);
        userInfoResponse.EnsureSuccessStatusCode();
        using var userInfoDocument = JsonDocument.Parse(
            await userInfoResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var userInfo = userInfoDocument.RootElement;
        Assert.Equal(ClaimValue(principal, "sub"), userInfo.GetProperty("sub").GetString());
        Assert.Equal(authentik.UserEmail, userInfo.GetProperty("email").GetString());
        Assert.True(userInfo.GetProperty("email_verified").GetBoolean());
    }

    private async Task<AuthorizationResult> AuthorizeAsync(
        IPage page,
        bool loginRequired,
        ConcurrentQueue<string> browserDiagnostics)
    {
        var verifier = GenerateSecret(48);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = GenerateSecret(32);
        var nonce = GenerateSecret(32);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = authentik.ClientId,
            ["redirect_uri"] = authentik.CallbackServer.AuthorizationCallbackUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var authorizationUri = new UriBuilder(
            new Uri(authentik.BaseAddress, "/application/o/authorize/"))
        {
            Query = string.Join(
                "&",
                query.Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")),
        }.Uri;

        await page.GotoAsync(
            authorizationUri.AbsoluteUri,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 120_000,
            });

        if (loginRequired)
        {
            var identificationStage = page.Locator("ak-stage-identification");
            var username = identificationStage.Locator("input[name=uidField]");
            await username.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            await username.FillAsync(authentik.UserName);

            var inlinePassword = identificationStage.Locator("input[name=password]");
            if (await inlinePassword.IsVisibleAsync())
            {
                await inlinePassword.FillAsync(authentik.UserPassword);
                await identificationStage.Locator("button[type=submit]").ClickAsync();
            }
            else
            {
                await identificationStage.Locator("button[type=submit]").ClickAsync();
                var passwordStage = page.Locator("ak-stage-password");
                var password = passwordStage.Locator("input[name=password]");
                await password.WaitForAsync(
                    new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 60_000,
                    });
                await password.FillAsync(authentik.UserPassword);
                await passwordStage.Locator("button[type=submit]").ClickAsync();
            }
        }

        using var callbackTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Uri callback;
        try
        {
            callback = await authentik.CallbackServer.WaitForAuthorizationCallbackAsync(
                callbackTimeout.Token);
        }
        catch (OperationCanceledException exception) when (callbackTimeout.IsCancellationRequested)
        {
            var body = await page.Locator("body").InnerTextAsync();
            var safeBody = body.Length <= 1_500 ? body : body[..1_500];
            var cookieMetadata = (await page.Context.CookiesAsync())
                .Select(cookie =>
                    $"{cookie.Name}[domain={cookie.Domain};secure={cookie.Secure};sameSite={cookie.SameSite}]");
            var executorState = await page.Locator("ak-flow-executor").EvaluateAsync<string>(
                "executor => JSON.stringify({"
                + "loading: executor.loading, "
                + "component: executor.challenge?.component, "
                + "responseErrorKeys: Object.keys(executor.challenge?.responseErrors ?? {})"
                + "})");
            throw new TimeoutException(
                $"Authentik did not redirect to the registered callback. "
                + $"Current path: {new Uri(page.Url).AbsolutePath}. "
                + $"Visible page text: {safeBody}. "
                + $"Cookies: {string.Join(", ", cookieMetadata)}. "
                + $"Executor state: {executorState}. "
                + $"Browser diagnostics: {string.Join(" | ", browserDiagnostics)}",
                exception);
        }

        var parameters = ParseQuery(callback.Query);
        Assert.False(parameters.ContainsKey("error"));
        Assert.Equal(state, parameters["state"]);
        Assert.False(string.IsNullOrWhiteSpace(parameters["code"]));
        return new AuthorizationResult(parameters["code"], verifier, nonce);
    }

    private static void AttachBrowserDiagnostics(
        IPage page,
        ConcurrentQueue<string> diagnostics)
    {
        page.Request += (_, request) =>
        {
            if (TryGetRelevantPath(request.Url, out var path))
            {
                diagnostics.Enqueue($"request {request.Method} {path}");
            }
        };
        page.Response += (_, response) =>
        {
            if (TryGetRelevantPath(response.Url, out var path))
            {
                diagnostics.Enqueue(
                    $"response {response.Request.Method} {path} {response.Status}");
            }
        };
        page.RequestFailed += (_, request) =>
        {
            if (TryGetRelevantPath(request.Url, out var path))
            {
                diagnostics.Enqueue($"failed {request.Method} {path}: {request.Failure}");
            }
        };
        page.PageError += (_, error) => diagnostics.Enqueue($"page error: {error}");
    }

    private static bool TryGetRelevantPath(string value, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        path = uri.AbsolutePath;
        return path.StartsWith("/api/v3/flows/executor/", StringComparison.Ordinal)
            || path.StartsWith("/application/o/", StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> ExchangeCodeAsync(string code, string verifier)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(authentik.BaseAddress, "/application/o/token/"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{authentik.ClientId}:{authentik.ClientSecret}")));
        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = authentik.CallbackServer.AuthorizationCallbackUri.AbsoluteUri,
                ["code_verifier"] = verifier,
            });

        using var client = authentik.CreateAnonymousClient();
        return await client.SendAsync(request, CancellationToken.None);
    }

    private async Task<ClaimsPrincipal> ValidateIdTokenAsync(string idToken)
    {
        using var client = authentik.CreateAnonymousClient();
        var jwksUri = new Uri(
            authentik.BaseAddress,
            $"/application/o/{AuthentikContainerFixture.ApplicationSlug}/jwks/");
        var jwks = new JsonWebKeySet(
            await client.GetStringAsync(jwksUri, CancellationToken.None));
        var parameters = new TokenValidationParameters
        {
            ClockSkew = TimeSpan.FromSeconds(30),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateAudience = true,
            ValidAudience = authentik.ClientId,
            ValidateIssuer = true,
            ValidIssuer = authentik.Authority.AbsoluteUri,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.Keys,
            ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };
        var principal = handler.ValidateToken(idToken, parameters, out var validatedToken);
        var jwt = Assert.IsType<JwtSecurityToken>(validatedToken);
        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);
        return principal;
    }

    private static async Task<IPlaywright> CreatePlaywrightAsync()
    {
        try
        {
            return await Playwright.CreateAsync();
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "Playwright could not start. Build this project and run its generated "
                + "playwright.ps1 install --with-deps chromium script before enabling "
                + "ARRCONTROL_RUN_AUTHENTIK_E2E.",
                exception);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] =
                parts.Length == 2
                    ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                    : string.Empty;
        }

        return result;
    }

    private static string GenerateSecret(int byteCount) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string? ClaimValue(ClaimsPrincipal principal, string claimType) =>
        principal.FindFirst(claimType)?.Value;

    private sealed record AuthorizationResult(string Code, string Verifier, string Nonce);
}
