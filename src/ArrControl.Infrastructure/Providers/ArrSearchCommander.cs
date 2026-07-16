using System.Security.Cryptography;
using System.Text.Json;
using ArrControl.Application.Providers;
using ArrControl.Application.Search;

namespace ArrControl.Infrastructure.Providers;

internal static class ArrSearchCommander
{
    public static async Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        string kind,
        string apiVersion,
        IReadOnlyList<string> providerKeys,
        CancellationToken cancellationToken)
    {
        if (providerKeys.Count is < 1 or > 100)
            return ProviderCallResult<ProviderSearchResult>.Failed(ProviderErrorCodes.InvalidResponse);
        var contract = SearchContract(kind, providerKeys[0]);
        if (contract is null)
            return ProviderCallResult<ProviderSearchResult>.Failed(ProviderErrorCodes.InvalidResponse);
        var (prefix, commandName, idsProperty) = contract.Value;
        var ids = new List<int>(providerKeys.Count);
        foreach (var key in providerKeys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(key[prefix.Length..], out var id) || id <= 0)
                return ProviderCallResult<ProviderSearchResult>.Failed(ProviderErrorCodes.InvalidResponse);
            ids.Add(id);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["name"] = commandName,
            [idsProperty] = ids,
        });
        try
        {
            using var response = await transport.PostJsonAsync(
                connection, $"api/{apiVersion}/command", body, cancellationToken);
            if (response.StatusCode is not 200 and not 201)
                return ProviderCallResult<ProviderSearchResult>.Failed(
                    response.StatusCode switch
                    {
                        401 => ProviderErrorCodes.Unauthorized,
                        403 => ProviderErrorCodes.Forbidden,
                        409 => ProviderErrorCodes.UpstreamConflict,
                        429 => ProviderErrorCodes.RateLimited,
                        _ => ProviderErrorCodes.Unknown,
                    }, response.RateLimit, response.StatusCode);
            try
            {
                using var document = JsonDocument.Parse(response.Body);
                if (!document.RootElement.TryGetProperty("id", out var id)
                    || !id.TryGetInt32(out var commandId) || commandId <= 0)
                    return ProviderCallResult<ProviderSearchResult>.Failed(ProviderErrorCodes.InvalidResponse);
                return ProviderCallResult<ProviderSearchResult>.Succeeded(
                    new ProviderSearchResult(commandId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    response.RateLimit, response.StatusCode);
            }
            catch (JsonException)
            {
                return ProviderCallResult<ProviderSearchResult>.Failed(ProviderErrorCodes.InvalidResponse);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static (string Prefix, string CommandName, string IdsProperty)? SearchContract(
        string kind,
        string firstProviderKey) => kind switch
    {
        "sonarr" => ("episode:", "EpisodeSearch", "episodeIds"),
        "radarr" => ("movie:", "MoviesSearch", "movieIds"),
        "lidarr" => ("album:", "AlbumSearch", "albumIds"),
        "readarr" => ("book:", "BookSearch", "bookIds"),
        "whisparr" when firstProviderKey.StartsWith("episode:", StringComparison.Ordinal) =>
            ("episode:", "EpisodeSearch", "episodeIds"),
        "whisparr" => ("movie:", "MoviesSearch", "movieIds"),
        _ => null,
    };
}
