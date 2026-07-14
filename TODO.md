# Implementation Backlog

Tasks are ordered. Codex must complete acceptance criteria and tests before checking a box; do not mark roadmap-only providers supported early.

## P0 — foundation

- [ ] Add EF Core initial migration for identity, instances, credentials, audit, outbox, and jobs.
- [ ] Implement local bootstrap/login/logout/refresh with Argon2id and secure cookie sessions.
- [ ] Implement OIDC Authorization Code + PKCE with Authentik integration tests.
- [ ] Implement RBAC permissions and instance-group scopes.
- [ ] Implement master-key validation and AES-GCM credential store; API secrets are write-only.
- [ ] Implement instance CRUD, SSRF policy, connection probe, and capability persistence.
- [ ] Implement Sonarr and Radarr typed clients, fixtures, probe and health adapters.
- [ ] Generate TypeScript client from OpenAPI and replace placeholder dashboard data.
- [ ] Add English/German resource catalogs, locale switch, timezone preference, and parity test.
- [ ] Add CI validation, image smoke test, SBOM, signing, CodeQL and Trivy workflows.
- [ ] Add database migration command and Compose one-shot migration service.

## P1 — core operations

- [ ] Durable leased poll scheduler with retry, jitter, timeouts, concurrency and checkpoints.
- [ ] Sonarr/Radarr catalog normalization and incremental synchronization.
- [ ] Missing query with cursor pagination, filters, saved views and freshness metadata.
- [ ] Aggregated queue/history and download correlation.
- [ ] Operation model with idempotency, dry-run, per-target results and cancellation.
- [ ] Search selected/instance/group/all with rate limits and explicit scope preview.
- [ ] Import-failure classification using deterministic rules only.
- [ ] Health incident grouping, acknowledge/snooze and remediation links.
- [ ] Transactional outbox and live event reconnect/snapshot protocol.
- [ ] Append-only audit queries, retention job and redacted diagnostics export.

## P2 — provider waves

- [ ] Lidarr, Readarr, Whisparr adapters and contract suites.
- [ ] Prowlarr and Bazarr adapters.
- [ ] SABnzbd, NZBGet, qBittorrent, Transmission and Deluge adapters.
- [ ] Plex, Jellyfin and Emby adapters.
- [ ] Overseerr/Jellyseerr, Ombi and Recyclarr adapters.
- [ ] Notification providers listed in `docs/PROVIDERS.md`.

## P3 — v1 hardening

- [ ] WCAG 2.2 AA audit and keyboard-only E2E suite.
- [ ] Performance/load targets and documented capacity envelope.
- [ ] Backup/restore and previous-version upgrade CI.
- [ ] Threat-model review and independent security review.
- [ ] Administrator/operator/user documentation and provider troubleshooting matrix.
- [ ] Release candidate compatibility report and signed multi-arch v1.0 image.
