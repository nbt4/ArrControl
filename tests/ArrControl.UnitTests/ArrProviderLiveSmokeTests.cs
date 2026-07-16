using ArrControl.Application.Providers;
using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Connections;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class ArrProviderLiveSmokeTests
{
    [Theory]
    [Trait("Category", "LiveProvider")]
    [InlineData("sonarr")]
    [InlineData("radarr")]
    [InlineData("lidarr")]
    [InlineData("readarr")]
    [InlineData("whisparr")]
    [InlineData("prowlarr")]
    [InlineData("bazarr")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("plex")]
    [InlineData("jellyfin")]
    [InlineData("emby")]
    [InlineData("overseerr")]
    [InlineData("jellyseerr")]
    [InlineData("ombi")]
    public async Task Opt_in_live_status_and_health_smoke_is_read_only(string kind)
    {
        var prefix = $"ARRCONTROL_LIVE_{kind.ToUpperInvariant()}_";
        var baseUrl = Environment.GetEnvironmentVariable(prefix + "BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable(prefix + "API_KEY");
        var username = Environment.GetEnvironmentVariable(prefix + "USERNAME");
        var password = Environment.GetEnvironmentVariable(prefix + "PASSWORD");
        if (baseUrl is null && apiKey is null && username is null && password is null)
        {
            return;
        }

        var allowPrivate = bool.TryParse(
            Environment.GetEnvironmentVariable(prefix + "ALLOW_PRIVATE_NETWORK"),
            out var privateValue) && privateValue;
        var tlsVerification = !bool.TryParse(
            Environment.GetEnvironmentVariable(prefix + "DISABLE_TLS_VERIFICATION"),
            out var disableTls) || !disableTls;
        var policy = new OutboundTargetPolicy(new SystemHostAddressResolver());
        var transport = new SafeProviderApiTransport(policy);
        IArrProviderClient client = kind switch
        {
            "sonarr" => new SonarrClient(transport),
            "radarr" => new RadarrClient(transport),
            "lidarr" => new LidarrClient(transport),
            "readarr" => new ReadarrClient(transport),
            "whisparr" => new WhisparrClient(transport),
            "prowlarr" => new ProwlarrClient(transport),
            "bazarr" => new BazarrClient(transport),
            "sabnzbd" => new SabnzbdClient(transport),
            "nzbget" => new NzbGetClient(transport),
            "qbittorrent" => new QBittorrentClient(transport),
            "transmission" => new TransmissionClient(transport),
            "deluge" => new DelugeClient(transport),
            "plex" => new PlexClient(transport),
            "jellyfin" => new JellyfinClient(transport),
            "emby" => new EmbyClient(transport),
            "overseerr" => new OverseerrClient(transport),
            "jellyseerr" => new JellyseerrClient(transport),
            "ombi" => new OmbiClient(transport),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var credentials = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(apiKey)) credentials[CredentialPurposes.ApiKey] = apiKey;
        if (!string.IsNullOrEmpty(username)) credentials[CredentialPurposes.Username] = username;
        if (!string.IsNullOrEmpty(password)) credentials[CredentialPurposes.Password] = password;
        var required = client is IProviderCredentialContract contract
            ? contract.RequiredCredentialPurposes
            : [CredentialPurposes.ApiKey];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || required.Any(value => !credentials.ContainsKey(value)))
        {
            throw new InvalidOperationException("Live provider smoke configuration is incomplete.");
        }
        var connection = new ProviderConnection(
            Guid.CreateVersion7(),
            baseUri,
            tlsVerification,
            allowPrivate,
            credentials);

        var status = await client.GetSystemStatusAsync(connection, CancellationToken.None);
        var health = await client.GetHealthAsync(connection, CancellationToken.None);

        Assert.True(status.Success, status.ErrorCode);
        Assert.True(health.Success, health.ErrorCode);
    }
}
