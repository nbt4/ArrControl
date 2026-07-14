# Provider Architecture and Compatibility

## Contract

Providers expose capability slices: `probe`, `library`, `missing`, `queue`, `history`, `search`, `grab`, `tasks`, `health`, `download-client`, `media-server`, and `notifications`. Capability discovery controls UI and API actions. Every call has timeout, cancellation, typed errors, redacted logging, and rate-limit metadata.

Credentials are referenced through a secret handle. Adapters never persist secrets or use global mutable HTTP clients. Provider DTOs stay inside adapter assemblies; mapping produces versioned canonical records.

## Planned compatibility matrix

| Family | Providers | Planned capabilities | Wave |
|---|---|---|---|
| Arr library | Sonarr, Radarr | full reference contract | 1 |
| Arr library | Lidarr, Readarr, Whisparr | library/missing/queue/search/health/tasks | 2 |
| Arr supporting | Prowlarr, Bazarr | indexer/health/history; subtitle activity | 2 |
| Usenet | SABnzbd, NZBGet | queue/history/pause/remove/retry | 2 |
| Torrent | qBittorrent, Transmission, Deluge | queue/history/pause/remove | 2 |
| Media servers | Plex, Jellyfin, Emby | library availability/activity/health | 3 |
| Ecosystem | Recyclarr | sync status/task execution | 3 |
| Requests | Overseerr/Jellyseerr, Ombi | request status and correlation | 3 |
| Notifications | email, webhook, Discord, Slack, Teams, Telegram, ntfy, Gotify, Pushover | outbound events | 3 |

“Supported” in release notes requires fixtures for at least two upstream versions, authentication failure coverage, unknown-field tolerance, rate-limit handling, and a live opt-in smoke test.

## Error taxonomy

`unreachable`, `tls_error`, `unauthorized`, `forbidden`, `rate_limited`, `unsupported_version`, `invalid_response`, `upstream_conflict`, `not_found`, `timeout`, and `unknown`. Raw upstream messages are diagnostic fields; localized user messages derive from stable codes.

## Plugin evolution

In-process signed assemblies are not loaded from arbitrary paths in v1. The first extension mechanism is source-level provider packages. A v2 provider host may use a versioned gRPC contract, isolated container, declared permissions, health handshake, and compatibility negotiation.
