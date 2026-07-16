# Implementation Backlog

Tasks are ordered. Codex must complete acceptance criteria and tests before checking a box; do not mark roadmap-only providers supported early.

## P0 — foundation

- [x] Add EF Core initial migration for identity, instances, credentials, audit, outbox, and jobs.
- [x] Implement local bootstrap/login/logout/refresh with Argon2id and secure cookie sessions.
- [x] Implement OIDC Authorization Code + PKCE with Authentik integration tests.
- [x] Implement RBAC permissions and instance-group scopes.
- [x] Implement master-key validation and AES-GCM credential store; API secrets are write-only.
- [x] Implement instance CRUD, SSRF policy, connection probe, and capability persistence.
- [x] Implement Sonarr and Radarr typed clients, fixtures, probe and health adapters.
- [x] Generate TypeScript client from OpenAPI and replace placeholder dashboard data.
- [x] Add English/German resource catalogs, locale switch, timezone preference, and parity test.
- [x] Add CI validation, image smoke test, SBOM, signing, CodeQL and Trivy workflows.
- [x] Add database migration command and Compose one-shot migration service.

## P1 — core operations

- [x] Durable leased poll scheduler with retry, jitter, timeouts, concurrency and checkpoints.
- [x] Sonarr/Radarr catalog normalization and incremental synchronization.
- [x] Missing query with cursor pagination, filters, saved views and freshness metadata.
- [x] Aggregated queue/history and download correlation.
- [x] Operation model with idempotency, dry-run, per-target results and cancellation.
- [x] Search selected/instance/group/all with rate limits and explicit scope preview.
- [x] Import-failure classification using deterministic rules only.
- [x] Health incident grouping, acknowledge/snooze and remediation links.
- [x] Transactional outbox and live event reconnect/snapshot protocol.
- [x] Append-only audit queries, retention job and redacted diagnostics export.

## P2 — provider waves

- [x] Lidarr, Readarr, Whisparr adapters and contract suites.
- [x] Prowlarr and Bazarr adapters.
- [x] SABnzbd, NZBGet, qBittorrent, Transmission and Deluge adapters.
- [x] Plex, Jellyfin and Emby adapters.
- [x] Overseerr/Jellyseerr, Ombi and Recyclarr adapters.
- [x] Notification providers listed in `docs/PROVIDERS.md`.

## P3 — v1 hardening

- [x] WCAG 2.2 AA audit and keyboard-only E2E suite.
- [x] Performance/load targets and documented capacity envelope.
- [x] Backup/restore and previous-version upgrade CI.
- [ ] Threat-model review and independent security review. Threat model plus independent automated CodeQL/Trivy/ZAP review are complete; independent human approval remains.
- [x] Administrator/operator/user documentation and provider troubleshooting matrix.
- [ ] Release candidate compatibility report and signed multi-arch v1.0 image. Report and guarded pipeline are complete; independent human approval plus explicitly authorized tag/push/publication remain.
