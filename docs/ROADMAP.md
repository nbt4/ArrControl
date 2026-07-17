# Roadmap

## v0.1 Foundation

Runnable monorepo, PostgreSQL migrations, local identity, OIDC/Authentik, RBAC, encrypted secrets, instance onboarding, Sonarr/Radarr probes, dashboard shell, English/German, CI images.

## v0.2 Core operations

Sonarr/Radarr library, missing, queues, health, search, operation/audit model, durable polling and jobs, live updates, bulk safety.

## v0.3 Ecosystem coverage

Lidarr/Readarr/Whisparr/Prowlarr/Bazarr, SABnzbd/NZBGet/qBittorrent/Transmission/Deluge, correlation and import triage.

## v0.4 Media and integrations

Contract adapters exist for Plex/Jellyfin/Emby and Overseerr/Jellyseerr/Ombi, but they are intentionally not selectable in the browser until each has a corresponding operational workflow. Notifications and the separate Recyclarr CLI boundary remain source-level integrations. Remaining integration work: notification routing/preferences, media/request workflows, calendar, and statistics.

## v1.0 Stable

Compatibility evidence, accessibility audit, performance targets, backup/restore validation, signed multi-arch release, upgrade guarantees, security review, operator documentation.

## Later

Isolated provider-host protocol, WebAuthn, high-availability worker topology, richer profile/tag synchronization, and curated extension registry. These are not commitments until accepted into a milestone.
