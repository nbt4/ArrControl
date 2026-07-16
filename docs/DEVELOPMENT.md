# Development Guide

Prerequisites: .NET 9 SDK, Node 22, pnpm 10, and Docker for the full test suite and local stack. Code and identifiers use English; user-facing copy uses translation keys.

```text
dotnet tool restore
dotnet restore ArrControl.slnx
dotnet test ArrControl.slnx
pnpm install
pnpm build
pnpm test:e2e
docker compose up --build
```

The focused Playwright accessibility gate needs Chromium. Install it once with `pnpm --filter @arrcontrol/web exec playwright install --with-deps chromium`; containerized runs can use the Playwright image pinned by the project. See `docs/ACCESSIBILITY.md` for scope and required manual release checks.

The 100,000-row capacity suite is deliberately opt-in: set `ARRCONTROL_RUN_PERFORMANCE_TESTS=1` and filter to `PerformanceEnvelopeTests`. It requires Docker, runs in an isolated PostgreSQL container, and normally takes under a minute on the documented reference host. See `docs/PERFORMANCE.md` before changing thresholds.

Before the first Compose start, generate the mounted credential key once with `umask 077; mkdir -p secrets; openssl rand -base64 32 > secrets/arrcontrol-master-key`. The `secrets/` directory is excluded from Git and the container build context; never commit or log its contents. Without credential-key settings the API may start for read-only metadata, but credential writes return `503 credential_encryption_unavailable` and provider reads fail closed.

Provider credentials are stored as independent write-only `api-key`, `username`, or `password` purposes. Configure only the purposes declared in `docs/PROVIDERS.md`: API keys for Arr/Prowlarr/Bazarr/SABnzbd/Plex/Jellyfin/Emby/Overseerr/Jellyseerr/Ombi, username and password for NZBGet/qBittorrent/Transmission, and the Web password for Deluge. Metadata reads show only whether a purpose is configured.

Branch names use `feature/`, `fix/`, or `docs/`. Conventional Commits are recommended. A change is done when tests, docs/API contract, localization, migration impact, threat impact, and observability are addressed. Generated clients are reproducible and never hand-edited.

Provider development starts with recorded redacted fixtures, typed DTOs, mapping tests, capability tests, and failure taxonomy tests. Live tests require explicit environment variables and never run against production libraries by default. Recyclarr is the CLI exception: test through `IRecyclarrProcessRunner`; never add an unofficial HTTP wrapper or free-form shell arguments.

Notification providers implement `INotificationProvider`. Tests must capture cloned requests before the adapter clears payload buffers, assert exact official payload/auth shapes, and prove that target/request/result object text contains no endpoint or secret. Teams tests target Workflows Adaptive Cards, not legacy connectors. SMTP tests use `ISmtpNotificationTransport`; real delivery requires implicit TLS and must never use a live mailbox in the normal suite.

Scheduled handlers implement `IScheduledJobHandler` with one stable type code and must honor cancellation. They receive an opaque claimed-job context and return checkpoint updates; they never manipulate leases or job rows directly. Add schedules with a five-field Cron expression, an IANA timezone, non-secret scope JSON, and a registered handler type. Cronos performs DST-aware occurrence calculation. Keep handler exceptions free of provider payloads: use `ScheduledJobException` with a stable non-secret code for expected failures.

The browser client is generated from `docs/api/openapi.yaml` with `pnpm generate:api` into `apps/web/src/api/generated.ts`; never edit that file by hand. `pnpm check:api` fails when the committed client is stale, and the production web/image build runs that check before TypeScript compilation. `openapi-fetch` supplies the typed runtime client with same-origin cookies. Dashboard metrics must be derived from API responses; absent authorization or failed requests render explicit states rather than placeholder counts.

English and German catalogs live in `apps/web/src/i18n` and use semantic keys. The parity test compares every key and interpolation placeholder. Anonymous language/timezone choices remain local to the browser; after login the persisted user values returned by `/auth/me` take precedence. `PUT /auth/preferences` replaces both values with CSRF protection, accepts only shipped locales and server-supported IANA timezone identifiers, and records a redacted audit event. Dates are formatted with `Intl` in the selected locale and timezone, and a language change takes effect without reloading the page.

EF Core tooling is pinned in `.config/dotnet-tools.json`. After a model change, generate migrations with the Infrastructure project and API startup project, then run `dotnet ef migrations has-pending-model-changes` with the same project pair. Migration integration tests require Docker and apply the schema to an ephemeral PostgreSQL 17 container. `dotnet run --project src/ArrControl.Api -- database migrate` (or `dotnet ArrControl.Api.dll database migrate`) is the explicit application-owned migration command; it takes a PostgreSQL advisory lock, applies all pending migrations, verifies none remain, and returns nonzero on failure. Normal API startup runs the same locked migration path before it starts HTTP or hosted services.

For a fresh local-identity database, apply migrations first and set both `Bootstrap__AdminEmail` and `Bootstrap__AdminPassword`. The password must be at least 12 characters and must not be the `.env.example` placeholder. On each startup, those settings create or synchronize the bootstrap administrator and revoke its existing sessions; they cannot create a second administrator. Secure cookies are unconditional, so exercise browser authentication through HTTPS. The flow is `GET /api/v1/auth/csrf`, then send the returned token as `X-CSRF-Token` with its cookie on login, refresh, and logout; use the replacement token returned after login or refresh.

Local-auth settings accept invariant `TimeSpan` values at `Auth__Local__AccessTokenLifetime`, `Auth__Local__RefreshTokenLifetime`, and `Auth__Local__LoginFailureWindow`, plus integer `AccountFailureLimit`, `IpFailureLimit`, `LoginRequestLimit`, and `SessionMutationRequestLimit` values in the same section. Defaults are 15 minutes, 30 days, 15 minutes, five persistent account failures, 20 persistent IP failures, 60 raw login requests, and 120 raw refresh/logout requests respectively. OIDC protocol callbacks use an independent per-IP counter with the session-mutation limit. All raw per-IP limits use the 15-minute login-failure window. Invalid or unsafe ranges fail startup.

## Authentik OIDC

Create a confidential Authentik OAuth2/OIDC provider with issuer mode `per_provider`, grant type `authorization_code`, claims included in the ID token, and an RSA Signing Key that emits RS256 tokens. Register these strict typed URLs, substituting the exact local HTTPS origin:

```text
authorization: https://arrcontrol.localhost/auth/oidc/callback
logout:        https://arrcontrol.localhost/auth/oidc/signed-out
```

Use scopes `openid profile email`. Authentik's default email mapping deliberately emits `email_verified=false`; automatic ArrControl linking therefore needs a custom mapping backed by a genuinely verified user attribute. The default profile mapping supplies group names. Configure the exact administrator group with `ARRCONTROL_OIDC_ADMIN_GROUP`, or indexed mappings such as `Auth__Oidc__RoleMappings__0__Group` and `Auth__Oidc__RoleMappings__0__Role`. Target roles must already exist.

Set `ARRCONTROL_OIDC_ENABLED=true`, the exact trailing-slash per-provider Authority, client ID/secret, `ARRCONTROL_PUBLIC_URL=https://arrcontrol.localhost`, and a Data Protection key path. Then start login at `/api/v1/auth/oidc/login?returnUrl=/`. Do not put the client secret in source control, logs, shell history, test fixtures, or browser configuration.

Fast OIDC tests use an in-process Authentik contract server and real signed tokens. The heavier Authentik suite starts only pinned ephemeral containers with generated credentials and runs provider-focused plus actual ArrControl-handler browser flows; it never reads the normal development `.env` or calls a live provider. See the test project README for its explicit opt-in commands when Docker has at least 2 CPU and 2 GiB available.

## Local HTTPS for browser authentication

The Compose application port is HTTP-only and Secure `__Host-` cookies are intentionally unusable there. One local option is Caddy with its development CA. Compose uses port `8080` by default; set `ARRCONTROL_HTTP_PORT` in `.env` to change it. Install Caddy, set `ARRCONTROL_PUBLIC_URL=https://arrcontrol.localhost` in `.env`, and use this `Caddyfile`:

```caddyfile
https://arrcontrol.localhost {
    tls internal
    reverse_proxy 127.0.0.1:8080
}
```

Run `caddy trust` once for the local CA, then `caddy run --config Caddyfile` and open `https://arrcontrol.localhost`. Do not reuse Caddy's internal CA or this setup for a public deployment; terminate publicly trusted TLS at the production reverse proxy described in `docs/OPERATIONS.md`.
