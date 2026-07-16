using ArrControl.Application.Identity;

namespace ArrControl.Api.Identity;

public sealed class OidcProviderSettings
{
    private OidcProviderSettings(
        bool enabled,
        Uri? authority,
        Uri? metadataAddress,
        string? clientId,
        string? clientSecret,
        Uri? publicOrigin,
        IReadOnlyCollection<OidcRoleMapping> roleMappings)
    {
        Enabled = enabled;
        Authority = authority;
        MetadataAddress = metadataAddress;
        ClientId = clientId;
        ClientSecret = clientSecret;
        PublicOrigin = publicOrigin;
        CallbackUri = publicOrigin is null
            ? null
            : new Uri(publicOrigin, OidcAuthenticationApi.CallbackPath.Value!);
        SignedOutCallbackUri = publicOrigin is null
            ? null
            : new Uri(publicOrigin, OidcAuthenticationApi.SignedOutCallbackPath.Value!);
        RoleMappings = roleMappings;
    }

    public bool Enabled { get; }

    public Uri? Authority { get; }

    public Uri? MetadataAddress { get; }

    public string? ClientId { get; }

    public string? ClientSecret { get; }

    public Uri? PublicOrigin { get; }

    public Uri? CallbackUri { get; }

    public Uri? SignedOutCallbackUri { get; }

    public IReadOnlyCollection<OidcRoleMapping> RoleMappings { get; }

    public static OidcProviderSettings Read(IConfiguration configuration)
    {
        var enabledValue = configuration["Auth:Oidc:Enabled"];
        if (enabledValue is not null && !bool.TryParse(enabledValue, out _))
        {
            throw new InvalidOperationException(
                "Configuration setting 'Auth:Oidc:Enabled' is not a valid boolean.");
        }

        var enabled = enabledValue is not null && bool.Parse(enabledValue);
        if (!enabled)
        {
            return new OidcProviderSettings(
                false,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<OidcRoleMapping>());
        }

        var authority = ReadHttpsUri(
            configuration["Auth:Oidc:Authority"],
            "Auth:Oidc:Authority",
            requireRootPath: false,
            requireTrailingSlash: true);
        var publicOrigin = ReadHttpsUri(
            configuration["App:PublicUrl"],
            "App:PublicUrl",
            requireRootPath: true,
            requireTrailingSlash: false);
        var clientId = ReadRequiredValue(
            configuration["Auth:Oidc:ClientId"],
            "Auth:Oidc:ClientId",
            512,
            rejectPlaceholder: false);
        var clientSecret = ReadRequiredValue(
            configuration["Auth:Oidc:ClientSecret"],
            "Auth:Oidc:ClientSecret",
            4096,
            rejectPlaceholder: true);
        var roleMappings = ReadRoleMappings(configuration);
        var identitySettings = new OidcIdentitySettings(roleMappings);
        identitySettings.Validate();

        return new OidcProviderSettings(
            true,
            authority,
            new Uri(authority, ".well-known/openid-configuration"),
            clientId,
            clientSecret,
            publicOrigin,
            roleMappings);
    }

    public override string ToString() => "[REDACTED]";

    private static IReadOnlyCollection<OidcRoleMapping> ReadRoleMappings(
        IConfiguration configuration)
    {
        var mappings = new List<OidcRoleMapping>();
        var administratorGroup = configuration["Auth:Oidc:AdministratorGroup"];
        if (!string.IsNullOrEmpty(administratorGroup))
        {
            mappings.Add(new OidcRoleMapping(
                administratorGroup,
                LocalIdentityConstants.AdministratorRoleName));
        }

        foreach (var item in configuration.GetSection("Auth:Oidc:RoleMappings").GetChildren())
        {
            var group = item["Group"];
            var role = item["Role"];
            if (string.IsNullOrEmpty(group) && string.IsNullOrEmpty(role))
            {
                continue;
            }

            mappings.Add(new OidcRoleMapping(group ?? string.Empty, role ?? string.Empty));
        }

        return mappings;
    }

    private static Uri ReadHttpsUri(
        string? value,
        string key,
        bool requireRootPath,
        bool requireTrailingSlash)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (requireRootPath && uri.AbsolutePath != "/")
            || (requireTrailingSlash && !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Configuration setting '{key}' must be an absolute HTTPS URL with the required exact path shape.");
        }

        return requireRootPath
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/")
            : uri;
    }

    private static string ReadRequiredValue(
        string? value,
        string key,
        int maximumLength,
        bool rejectPlaceholder)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl)
            || (rejectPlaceholder && value.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Configuration setting '{key}' must contain a non-placeholder value.");
        }

        return value;
    }
}
