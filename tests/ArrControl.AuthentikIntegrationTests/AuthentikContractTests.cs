using System.Text.Json;
using Xunit;

namespace ArrControl.AuthentikIntegrationTests;

[Collection(AuthentikContainerCollection.Name)]
public sealed class AuthentikContractTests(AuthentikContainerFixture authentik)
{
    [AuthentikContainerFact]
    public async Task Discovery_exposes_the_pinned_per_provider_oidc_contract()
    {
        using var client = authentik.CreateAnonymousClient();
        using var response = await client.GetAsync(
            new Uri(authentik.Authority, ".well-known/openid-configuration"),
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = document.RootElement;

        Assert.Equal(authentik.Authority.AbsoluteUri, root.GetProperty("issuer").GetString());
        Assert.Equal(
            new Uri(authentik.BaseAddress, "/application/o/authorize/").AbsoluteUri,
            root.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(
            new Uri(authentik.BaseAddress, "/application/o/token/").AbsoluteUri,
            root.GetProperty("token_endpoint").GetString());
        Assert.Equal(
            new Uri(authentik.BaseAddress, "/application/o/userinfo/").AbsoluteUri,
            root.GetProperty("userinfo_endpoint").GetString());
        Assert.Equal(
            new Uri(
                authentik.BaseAddress,
                $"/application/o/{AuthentikContainerFixture.ApplicationSlug}/jwks/").AbsoluteUri,
            root.GetProperty("jwks_uri").GetString());
        Assert.Equal(
            new Uri(
                authentik.BaseAddress,
                $"/application/o/{AuthentikContainerFixture.ApplicationSlug}/end-session/").AbsoluteUri,
            root.GetProperty("end_session_endpoint").GetString());

        AssertJsonArrayContains(root, "response_types_supported", "code");
        AssertJsonArrayContains(root, "grant_types_supported", "authorization_code");
        AssertJsonArrayContains(root, "code_challenge_methods_supported", "S256");
        AssertJsonArrayContains(root, "token_endpoint_auth_methods_supported", "client_secret_basic");
        AssertJsonArrayContains(root, "scopes_supported", "openid");
        AssertJsonArrayContains(root, "scopes_supported", "profile");
        AssertJsonArrayContains(root, "scopes_supported", "email");
    }

    [AuthentikContainerFact]
    public async Task Bootstrap_api_observes_the_strict_blueprint_configuration()
    {
        using var client = authentik.CreateApiClient();
        using var providerDocument = await GetApiDocumentAsync(
            client,
            $"api/v3/providers/oauth2/?client_id={Uri.EscapeDataString(authentik.ClientId)}");
        var provider = Assert.Single(
            providerDocument.RootElement.GetProperty("results").EnumerateArray().ToArray());

        Assert.Equal("arrcontrol-e2e", provider.GetProperty("name").GetString());
        Assert.Equal("confidential", provider.GetProperty("client_type").GetString());
        Assert.Equal("per_provider", provider.GetProperty("issuer_mode").GetString());
        Assert.Equal("hashed_user_id", provider.GetProperty("sub_mode").GetString());
        Assert.True(provider.GetProperty("include_claims_in_id_token").GetBoolean());
        Assert.Equal(authentik.ClientId, provider.GetProperty("client_id").GetString());
        Assert.NotEqual(JsonValueKind.Null, provider.GetProperty("signing_key").ValueKind);

        var grantTypes = provider.GetProperty("grant_types")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal("authorization_code", Assert.Single(grantTypes));

        var redirectUris = provider.GetProperty("redirect_uris")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            redirectUris,
            redirect => IsStrictRedirect(
                redirect,
                authentik.CallbackServer.AuthorizationCallbackUri,
                "authorization"));
        Assert.Contains(
            redirectUris,
            redirect => IsStrictRedirect(
                redirect,
                authentik.CallbackServer.PostLogoutUri,
                "logout"));
        Assert.Contains(
            redirectUris,
            redirect => IsStrictRedirect(
                redirect,
                AuthentikContainerFixture.ArrControlCallbackUri,
                "authorization"));
        Assert.Contains(
            redirectUris,
            redirect => IsStrictRedirect(
                redirect,
                AuthentikContainerFixture.ArrControlPostLogoutUri,
                "logout"));

        using var applicationDocument = await GetApiDocumentAsync(
            client,
            $"api/v3/core/applications/?slug={AuthentikContainerFixture.ApplicationSlug}");
        var application = Assert.Single(
            applicationDocument.RootElement.GetProperty("results").EnumerateArray().ToArray());
        Assert.Equal(
            AuthentikContainerFixture.ApplicationSlug,
            application.GetProperty("slug").GetString());
        Assert.Equal(
            provider.GetProperty("pk").GetInt32(),
            application.GetProperty("provider").GetInt32());

        using var mappingDocument = await GetApiDocumentAsync(
            client,
            "api/v3/propertymappings/provider/scope/?managed="
                + "arrcontrol.test%2Fproviders%2Foauth2%2Fscope-verified-email");
        var mapping = Assert.Single(
            mappingDocument.RootElement.GetProperty("results").EnumerateArray().ToArray());
        Assert.Equal("email", mapping.GetProperty("scope_name").GetString());
        Assert.Contains(
            mapping.GetProperty("pk").GetString(),
            provider.GetProperty("property_mappings")
                .EnumerateArray()
                .Select(item => item.GetString()));

        using var userDocument = await GetApiDocumentAsync(
            client,
            $"api/v3/core/users/?username={Uri.EscapeDataString(authentik.UserName)}");
        var user = Assert.Single(
            userDocument.RootElement.GetProperty("results").EnumerateArray().ToArray());
        Assert.Equal(authentik.UserEmail, user.GetProperty("email").GetString());
        Assert.True(user.GetProperty("is_active").GetBoolean());
        Assert.Equal("internal", user.GetProperty("type").GetString());
    }

    private static async Task<JsonDocument> GetApiDocumentAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    private static void AssertJsonArrayContains(JsonElement root, string propertyName, string value)
    {
        Assert.Contains(
            value,
            root.GetProperty(propertyName)
                .EnumerateArray()
                .Select(item => item.GetString()));
    }

    private static bool IsStrictRedirect(JsonElement redirect, Uri expected, string expectedType) =>
        string.Equals(
            redirect.GetProperty("matching_mode").GetString(),
            "strict",
            StringComparison.Ordinal)
        && string.Equals(
            redirect.GetProperty("url").GetString(),
            expected.AbsoluteUri,
            StringComparison.Ordinal)
        && string.Equals(
            redirect.GetProperty("redirect_uri_type").GetString(),
            expectedType,
            StringComparison.Ordinal);
}
