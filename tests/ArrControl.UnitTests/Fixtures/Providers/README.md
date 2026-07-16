# Arr library provider contract fixtures

These redacted, synthetic-minimal fixtures are derived from the official tagged v3 OpenAPI contracts. They contain no real URL, API key, filesystem path, library name, or user data. Extra fields in the newer fixtures deliberately verify forward-compatible deserialization; the `critical` health severity verifies unknown-enum normalization to `unknown`.

Contract sources:

- Sonarr [`v4.0.1.1168`](https://github.com/Sonarr/Sonarr/blob/v4.0.1.1168/src/Sonarr.Api.V3/openapi.json) and [`v4.0.19.2979`](https://github.com/Sonarr/Sonarr/blob/v4.0.19.2979/src/Sonarr.Api.V3/openapi.json).
- Radarr [`v5.3.1.8438`](https://github.com/Radarr/Radarr/blob/v5.3.1.8438/src/Radarr.Api.V3/openapi.json) and [`v6.3.0.10514`](https://github.com/Radarr/Radarr/blob/v6.3.0.10514/src/Radarr.Api.V3/openapi.json).
- Lidarr [`v2.9.6.4552`](https://github.com/Lidarr/Lidarr/blob/v2.9.6.4552/src/Lidarr.Api.V1/openapi.json) and [`v3.1.3.4975`](https://github.com/Lidarr/Lidarr/blob/v3.1.3.4975/src/Lidarr.Api.V1/openapi.json).
- Readarr [`v0.4.17.2801`](https://github.com/Readarr/Readarr/blob/v0.4.17.2801/src/Readarr.Api.V1/openapi.json) and [`v0.4.18.2805`](https://github.com/Readarr/Readarr/blob/v0.4.18.2805/src/Readarr.Api.V1/openapi.json).
- Whisparr [`v2.2.0-release.108`](https://github.com/Whisparr/Whisparr/blob/v2.2.0-release.108/src/Whisparr.Api.V3/openapi.json) and [`v3.1.0.2116`](https://github.com/Whisparr/Whisparr/blob/v3.1.0.2116/src/Whisparr.Api.V3/openapi.json).
- Prowlarr [`v2.4.0.5397`](https://github.com/Prowlarr/Prowlarr/blob/v2.4.0.5397/src/Prowlarr.Api.V1/openapi.json) and [`v2.5.1.5464`](https://github.com/Prowlarr/Prowlarr/blob/v2.5.1.5464/src/Prowlarr.Api.V1/openapi.json).
- Bazarr [`v1.5.3`](https://github.com/morpheus65535/bazarr/tree/v1.5.3/bazarr/api) and [`v1.6.0`](https://github.com/morpheus65535/bazarr/tree/v1.6.0/bazarr/api).
- SABnzbd [`4.5.3`](https://github.com/sabnzbd/sabnzbd/tree/4.5.3) and [`5.0.4`](https://github.com/sabnzbd/sabnzbd/tree/5.0.4), following the [5.0 API contract](https://sabnzbd.org/wiki/configuration/5.0/api).
- NZBGet [`v25.4`](https://github.com/nzbgetcom/nzbget/blob/v25.4/docs/api/API.md) and [`v26.2`](https://github.com/nzbgetcom/nzbget/blob/v26.2/docs/api/API.md).
- qBittorrent [`release-5.1.4`](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29) and [`release-5.2.3`](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29).
- Transmission [`4.0.6`](https://github.com/transmission/transmission/blob/4.0.6/docs/rpc-spec.md) and [`4.1.3`](https://github.com/transmission/transmission/blob/4.1.3/docs/rpc-spec.md).
- Deluge [`deluge-2.1.1`](https://github.com/deluge-torrent/deluge/blob/deluge-2.1.1/docs/source/devguide/how-to/curl-jsonrpc.md) and [`deluge-2.2.0`](https://github.com/deluge-torrent/deluge/blob/deluge-2.2.0/docs/source/devguide/how-to/curl-jsonrpc.md).
- Plex Media Server `1.41.9.9961` and [`1.43.2.10687`](https://hub.docker.com/r/plexinc/pms-docker/tags), using the official [PMS API 1.0](https://developer.plex.tv/pms/) contract.
- Jellyfin [`v10.10.7`](https://github.com/jellyfin/jellyfin/tree/v10.10.7/Jellyfin.Api/Controllers) and [`v10.11.10`](https://github.com/jellyfin/jellyfin/tree/v10.11.10/Jellyfin.Api/Controllers).
- Emby [`4.8.11.0`](https://github.com/MediaBrowser/Emby.Releases/releases/tag/4.8.11.0) and [`4.9.3.0`](https://github.com/MediaBrowser/Emby.Releases/releases/tag/4.9.3.0), using the official [REST API](https://dev.emby.media/doc/restapi/index.html).

The fixtures cover status, health, library, queue, and history resources. Search and download-client mutation bodies are checked against the official command types. Arr-family/Prowlarr/Bazarr use `X-Api-Key`; the download clients use their contract-specific query, Basic, session-cookie, or CSRF flow.
