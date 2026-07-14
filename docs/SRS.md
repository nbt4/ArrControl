# Software Requirements Specification

## 1. Purpose

ArrControl is a self-hosted operations center for media automation. A user manages any number of heterogeneous service instances from one responsive, localized UI and one versioned API.

## 2. Goals and success measures

- A routine operator does not need upstream UIs for missing searches, queue inspection, import triage, health, or task execution.
- Aggregated reads remain useful when an upstream is temporarily unavailable by showing timestamped last-known state.
- A destructive or broad action is explicit, authorized, idempotent where possible, and auditable.
- 95th-percentile cached dashboard response is below 500 ms for 20 instances and 100,000 library items.
- Freshness is visible; default poll intervals are 30 s queue, 5 min health, and 15 min library deltas.

## 3. Personas and roles

- Administrator: security, users, providers, schedules, retention, upgrades.
- Operator: searches, queue/import actions, task execution, non-destructive library actions.
- Viewer: read-only dashboards and diagnostics.
- Custom roles: permission bundles scoped globally or to instance groups.

## 4. Functional requirements

### FR-01 Instance management

Create, update, disable, test, group, and delete any number of provider instances. Secrets are write-only. Connections support custom CA bundles, proxy configuration, and explicit TLS verification controls with warnings.

### FR-02 Unified library and missing

Normalize movies, series, seasons, episodes, artists, albums, books, and adult-media entities without erasing provider-specific data. Provide filtering, sorting, saved views, pagination, and source attribution. Search one item, selected items, an instance, a group, or all eligible instances.

### FR-03 Queue and import failures

Aggregate provider and download-client queues. Correlate by download ID and provider tracking ID. Show progress, ETA, protocol, client, source instance, status, and raw diagnostic details. Supported actions: retry, remove/blocklist, manual import link-out, and re-search; capability checks determine availability.

### FR-04 Health and tasks

Collect health checks and scheduled-task state. Deduplicate related incidents while preserving sources. Permit safe provider tasks and show execution progress.

### FR-05 Search center

Federate interactive searches across compatible providers; normalize releases while retaining protocol, indexer, age, size, seeders, quality, languages, custom-format score, and rejection reasons. Grabs require confirmation and audit.

### FR-06 Scheduling

Create timezone-aware schedules for missing search, failed-item retry, library refresh, health poll, and maintenance. Apply concurrency limits, jitter, pause windows, and instance-scoped rate limits.

### FR-07 Authentication and authorization

Support local email/password accounts and standards-compliant OIDC Authorization Code + PKCE (Authentik reference). Link accounts only through verified claims or explicit admin action. Enforce RBAC server-side.

### FR-08 Localization and accessibility

English and German ship in v1; runtime locale switching and contributor-added locales require no rebuild of backend resources. User locale, timezone, number, date, and byte formats are respected. WCAG 2.2 AA is the target.

### FR-09 Audit and notifications

Record actor, action, scope, correlation ID, redacted before/after summary, outcome, IP, and timestamp. Notification providers: email, generic webhook, Discord, Slack, Teams, Telegram, ntfy, Gotify, Pushover. Secrets never enter audit payloads.

### FR-10 Live updates and API

Expose REST under `/api/v1` and SignalR/SSE-style events with reconnect and snapshot recovery. Publish OpenAPI. Use cursor pagination for volatile/high-volume resources.

## 5. Non-functional requirements

- Security: OWASP ASVS L2 target; least privilege; encryption in transit; envelope-encrypted provider secrets.
- Reliability: PostgreSQL is authoritative; jobs use leases and retry with capped exponential backoff; graceful degradation.
- Observability: structured logs, OpenTelemetry traces/metrics, health endpoints, correlation IDs; no secrets or media paths at default log level.
- Portability: Linux amd64/arm64 OCI images; Docker Compose baseline; Kubernetes-ready configuration.
- Compatibility: current and previous major upstream API where practical; compatibility matrix is release evidence, not marketing text.
- Maintainability: provider contract tests, architecture tests, generated API client, migrations reviewed like code.
- Privacy: no telemetry by default; diagnostics export is opt-in and redacted.

## 6. Constraints and exclusions

- No iframe-based upstream UI embedding.
- No bypass of indexer, provider, or media licensing rules.
- v1 does not replace deep upstream configuration such as quality-profile authoring.
- Automatic filesystem repair and arbitrary command execution are excluded.
- “Retry import” is only exposed when the upstream has a safe documented capability; otherwise diagnostic guidance/link-out is shown.

## 7. Acceptance baseline for v1

Fresh install, local admin bootstrap, Authentik login, instance onboarding, aggregated dashboard/missing/queue/health, scoped mass search, audit log, English/German, backup/restore documentation, upgrade migration, signed multi-arch images, SBOM, and passing reference-provider contract tests.
