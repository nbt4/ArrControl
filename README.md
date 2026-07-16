# ArrControl

ArrControl is a free, open-source operations center for multiple Arr instances, download clients, and media servers. It unifies missing media, queues, health, searches, scheduled jobs, and audit history without embedding upstream UIs.

This repository is an implementation-ready blueprint **and** a runnable vertical slice. The current foundation includes migrations, local and Authentik-compatible OIDC identity, opaque browser sessions, database-backed RBAC with global and instance-group scopes, an encrypted write-only multi-purpose credential store, scoped instance/group management with a DNS-rebinding-resistant connection probe, typed Arr/supporting/download-client/media-server/request-manager/notification adapters plus a bounded Recyclarr CLI boundary, durable catalog and activity synchronization, an RBAC-scoped cursor-paginated missing API with saved views/freshness, aggregated queue/history with download correlation, durable grouped health incidents with acknowledgement/snooze/remediation, a transactional outbox with RBAC-filtered replayable SSE updates, append-only audit queries/retention and strict-redaction diagnostics export, and an English/German generated-client dashboard with real authentication and persisted locale/timezone preferences; the provider contracts, API contract, delivery pipeline, and backlog define the path to the complete product.

## Quick start

1. Copy `.env.example` to `.env` and change every placeholder secret.
2. Create the credential master key with `umask 077; mkdir -p secrets; openssl rand -base64 32 > secrets/arrcontrol-master-key`.
3. Run `docker compose up --build`; the one-shot `migrate` service applies schema changes before the application starts.
4. Open `http://localhost:8080` (API: `http://localhost:8080/api/v1/system/status`).

The direct HTTP port is suitable for status checks but deliberately cannot set the Secure `__Host-` login cookies. Put HTTPS in front of it before testing browser authentication; a concrete Caddy-based local setup is documented in [Local HTTPS for browser authentication](docs/DEVELOPMENT.md#local-https-for-browser-authentication).

For local development, use .NET 9 SDK and Node 22/pnpm 10. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Add to an existing Arr stack

The published `nobentie/arrcontrol` image contains the browser UI, API, and background workers. A single PostgreSQL container is the only required companion; Authentik, Redis, and message brokers are not required.

1. Copy [deploy/compose.arr-stack.yaml](deploy/compose.arr-stack.yaml) next to your stack and copy [deploy/arr-stack.env.example](deploy/arr-stack.env.example) to `.env`.
2. Generate the write-only credential key once: `mkdir -p /opt/docker/arrcontrol/secrets && openssl rand -base64 32 > /opt/docker/arrcontrol/secrets/master-key && chmod 600 /opt/docker/arrcontrol/secrets/master-key`.
3. Adjust the data path, strong database/admin passwords, and public HTTPS URL. The fragment attaches ArrControl to existing external Docker networks named `proxy` and `starr`; rename those entries if your stack uses different names.
4. Start the database, apply migrations with the same application image, then start both services:

   ```text
   docker compose -f compose.arr-stack.yaml up -d arrcontrol-db
   docker compose -f compose.arr-stack.yaml run --rm --no-deps arrcontrol database migrate
   docker compose -f compose.arr-stack.yaml up -d
   ```

Your reverse proxy should forward HTTPS traffic to `arrcontrol:8080` on the `proxy` network. Browser login intentionally does not work over direct HTTP. For Arr containers using `network_mode: service:gluetun`, add their Gluetun service name and published internal port (for example `http://arr_vpn:8989` for Sonarr), then explicitly permit private-network access in ArrControl.

## Documentation map

- [Documentation index](docs/README.md)
- [User guide](docs/USER_GUIDE.md), [operator guide](docs/OPERATOR_GUIDE.md), and [administrator guide](docs/ADMIN_GUIDE.md)
- [Provider troubleshooting](docs/PROVIDER_TROUBLESHOOTING.md)
- [Product requirements](docs/SRS.md)
- [Architecture and decisions](docs/SDD.md)
- [Data model](docs/DATA_MODEL.md)
- [Provider architecture](docs/PROVIDERS.md)
- [API contract](docs/api/openapi.yaml)
- [UI system](docs/UI_UX.md)
- [Security and authentication](docs/SECURITY.md)
- [Threat model and review status](docs/THREAT_MODEL.md)
- [Security reporting policy](SECURITY.md)
- [Operations and delivery](docs/OPERATIONS.md)
- [Backup/restore](docs/BACKUP_RESTORE.md), [capacity evidence](docs/PERFORMANCE.md), and [accessibility audit](docs/ACCESSIBILITY.md)
- [v1 compatibility report and release checklist](docs/release/V1_COMPATIBILITY_REPORT.md)
- [Roadmap](docs/ROADMAP.md) and [implementation backlog](TODO.md)
- [Codex master prompt](.codex/MASTER_PROMPT.md)

## Scope statement

“Support all services” means a stable provider architecture plus the compatibility matrix in `docs/PROVIDERS.md`. Sonarr, Radarr, Lidarr, Readarr, Whisparr, Prowlarr, Bazarr, SABnzbd, NZBGet, qBittorrent, Transmission, Deluge, Plex, Jellyfin, Emby, Overseerr, Jellyseerr/Seerr, and Ombi currently have contract-tested HTTP capability slices; Recyclarr has a separate contract-tested CLI task boundary because upstream exposes no service API. Other providers remain roadmap items until their contract tests pass.

## License

Licensed under the MIT License. Everyone may use, study, modify, and redistribute ArrControl free of charge under its terms. See [LICENSE](LICENSE).

