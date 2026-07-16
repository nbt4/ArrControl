# ArrControl v1.0 Release-Candidate Compatibility Report

Candidate: `1.0.0`  
Assessment date: 2026-07-16  
Status: technically ready for a tagged release after independent human security approval; no image has been published from this workspace.

## Platform envelope

| Component | Candidate baseline | Evidence |
| --- | --- | --- |
| API/runtime | ASP.NET Core/.NET 9, Linux container, invariant globalization disabled | warnings-as-errors solution/API builds; non-root read-only image workflow |
| Browser | React 19, Chromium accessibility baseline, responsive English/German UI | lint, 4 component/catalog tests, production build, 2 keyboard/axe E2E scenarios |
| Database | PostgreSQL 17; forward EF migrations; 256 MiB shared memory in Compose | model/migration drift check; isolated dump/restore/initial-schema upgrade; idempotent migration |
| Capacity | 20 instances, 100,000 catalog rows, 10,000-target operation, 250 reconnect burst | 259.1 ms cached projection p95; 6.98 s bulk snapshot; 351.3 ms reconnect p95 on documented host |
| Architectures | OCI `linux/amd64` and `linux/arm64` | tag workflow build matrix, plus a local OCI multi-arch build in this workspace that verified both manifests; publication evidence is pending |
| Supply chain | CycloneDX SBOM, BuildKit max provenance, Trivy gate, keyless Cosign digest signature | workflow is commit-pinned and verifies the exact tag-workflow identity; published digest/signature pending |

## Provider contract evidence

The focused 2026-07-16 contract run passed 111/111 tests with no skips. Fixtures are synthetic-minimal redacted projections of the linked official tagged contracts in `docs/PROVIDERS.md`.

| Provider | Fixture versions | Contracted v1 slice |
| --- | --- | --- |
| Sonarr | 4.0.1.1168, 4.0.19.2979 | probe, health, catalog/missing, queue/history, missing-ID search |
| Radarr | 5.3.1.8438, 6.3.0.10514 | probe, health, catalog/missing, queue/history, missing-ID search |
| Lidarr | 2.9.6.4552, 3.1.3.4975 | probe, health, artist/album catalog, queue/history, album search |
| Readarr | 0.4.17.2801, 0.4.18.2805 | probe, health, author/book catalog, queue/history, book search |
| Whisparr | 2.2.0.108, 3.1.0.2116 | v2 series/episode and v3 movie models, health/activity/search |
| Prowlarr | 2.4.0.5397, 2.5.1.5464 | probe, redacted health/indexers/history |
| Bazarr | 1.5.3, 1.6.0 | probe, path-redacted health, daily subtitle activity |
| SABnzbd | 4.5.3, 5.0.4 | health, queue/history, pause/resume/remove/retry |
| NZBGet | 25.4, 26.2 | health, queue/history, pause/resume/remove/retry |
| qBittorrent | 5.1.4, 5.2.3 | health, current/completed activity, pause/resume/remove |
| Transmission | 4.0.6, 4.1.3 | health, current/completed activity, 409 session challenge, mutations |
| Deluge | 2.1.1, 2.2.0 | health, current/completed activity, login/daemon selection, mutations |
| Plex | 1.41.9.9961, 1.43.2.10687 | aggregate availability/playback health |
| Jellyfin | 10.10.7, 10.11.10 | aggregate availability/playback health |
| Emby | 4.8.11.0, 4.9.3.0 | aggregate availability/playback health |
| Overseerr | 1.34.0, 1.35.0 | probe, health, privacy-preserving request inventory |
| Jellyseerr/Seerr | 2.7.3, 3.3.0 | probe, health, privacy-preserving request inventory |
| Ombi | 4.47.1, 4.48.0 | probe, health, separate movie/TV request inventory |
| Recyclarr CLI | 7.4.1, 8.7.0 | version gate, preview/sync allowlist, injection/redaction/timeout boundary |
| Notifications | SMTP plus webhook, Discord, Slack, Teams Workflows, Telegram, ntfy, Gotify, Pushover | exact payload/auth, bounded errors, SSRF/redaction; routing is not delivered |

Unknown fields/statuses fail soft where safe; an unknown product or unevidenced future major fails closed. Optional live smoke tests exist for every supported HTTP provider but were not run in this workspace because no isolated provider credentials/endpoints were supplied. Therefore this report establishes contract compatibility, not a claim about an operator's particular installation.

## Functional and security evidence

- Local bootstrap/session rotation/replay controls, Authentik-shaped OIDC + optional pinned real-Authentik suite, current-database RBAC scopes, encrypted write-only credential store, SSRF/DNS-rebinding defenses, durable scheduler, projections, operations, outbox/SSE, audit/diagnostics, and provider boundaries have focused unit/integration suites described in `docs/TESTING.md`.
- OpenAPI/generated-client drift, migration drift, frontend production build, security headers, and the data lifecycle are automated gates.
- OWASP ZAP local passive baseline: 0 failures; the same-origin resource-policy warning found during review was fixed and regression-tested. CodeQL/Trivy workflows remain the authoritative CI scans.
- Local OCI release build in this workspace produced a multi-architecture archive whose manifest list contains `linux/amd64` and `linux/arm64`. This confirms the release Dockerfile and build pipeline still assemble both target architectures locally before any tag/push/publish step.
- WCAG baseline: zero axe violations across the two delivered anonymous/authenticated scenarios; manual assistive-technology RC pass remains required by `ACCESSIBILITY.md`.
- Threat model is complete. `docs/release/SECURITY_REVIEW.md` deliberately remains pending until a reviewer independent of implementation signs off.

## Known scope boundaries

- The browser currently exposes the overview/authentication/preferences, incident disclosure, and audit disclosure slice; several navigation destinations are intentionally disabled rather than placeholder implementations.
- Notification routing/preferences/durable delivery selection are not delivered; adapters do not send automatically.
- Recyclarr is not bundled or automatically executed.
- No arbitrary provider task execution, interactive release grab, automatic filesystem repair, retry-import guess, TOTP/WebAuthn, or Authentik back-channel logout is claimed.
- Contract tests, not live production instances, are the release compatibility evidence in this workspace.

## Release decision

The candidate may be tagged `v1.0.0` only after the independent human security record says `Status: approved`, the maintainer approves publication, the source tree is committed/reviewed, and required CI is green. The tag workflow then publishes the multi-architecture digest, scans it, signs it with GitHub OIDC, verifies identity, asserts both architectures, and uploads immutable evidence. The local workspace already confirms the release container can be built for both `linux/amd64` and `linux/arm64`; until publication happens, do not call the v1.0 image published or signed.
