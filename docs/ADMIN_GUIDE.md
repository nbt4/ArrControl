# Administrator Guide

This guide covers installation ownership, identity, authorization, instances, lifecycle, and recovery. Read `SECURITY.md`, `OPERATIONS.md`, `BACKUP_RESTORE.md`, and `PERFORMANCE.md` before exposing ArrControl outside a test network.

## Install and bootstrap

1. Pin an ArrControl image version/digest and PostgreSQL 17. Copy `.env.example`; replace every `CHANGE_ME` value.
2. Generate the credential key with `umask 077; mkdir -p secrets; openssl rand -base64 32 > secrets/arrcontrol-master-key`. Keep it mode `0600`, outside source control and backups that contain only public configuration.
3. Put a trusted HTTPS reverse proxy in front of port 8080. Browser login intentionally does not work over direct HTTP because session cookies are always Secure.
4. Run `docker compose up --build`. Confirm `migrate` exits zero, `/health/live` is live, and `/health/ready` is healthy.
5. Bootstrap once with a unique administrator email/password. Log in, confirm the Administrator role, then remove both bootstrap variables and delete the cleartext password from deployment configuration.
6. Take a first complete backup and perform an isolated restore test.

Do not run the app with a database schema newer than its image. Normal HTTP startup never migrates; the explicit one-shot command owns schema changes.

## Identity and authorization

Local identity is the recovery path. OIDC is optional and disabled by default. For Authentik, configure a confidential Authorization Code provider with PKCE-compatible redirects, RS256 signing, exact issuer/public URLs, and verified email claims. Test an exact group-to-role mapping before relying on OIDC; never grant a role from unverified email or a fuzzy group match.

Built-in roles are immutable: Administrator has all permissions, Operator has operational reads/search/queue/task actions, and Viewer has instance/library reads. Custom roles contain only catalog permissions. Assignments are global or attached to one instance group; ungrouped instances require global grants. ArrControl prevents removal of the final active global authorization manager. Keep at least one tested local global administrator.

Review authorization audit actions after every role or assignment change. A button hidden in the UI is not the security boundary; the API evaluates current database grants on every request.

## Instance onboarding

Create groups first, then add an instance with its provider kind, base URL, TLS policy, and optional group. URLs must not contain credentials, query strings, or fragments. Private RFC1918/ULA targets require the explicit private-network switch; loopback, metadata, link-local, CGNAT, reserved, mixed unsafe DNS, redirects, and DNS rebinding remain blocked.

Write only credential purposes documented for that provider in `PROVIDERS.md`. The API never returns their values. Run a connection probe and inspect the capability report before expecting an action. An HTTP response alone is not compatibility evidence: the adapter must validate the product and supported version. Changing kind/URL/TLS/private-network policy invalidates old capabilities.

Keep TLS verification enabled. A private CA must be solved at the deployment trust boundary; do not silently downgrade verification. Read `PROVIDER_TROUBLESHOOTING.md` before changing a working configuration.

## Scheduling and operations

The embedded worker uses PostgreSQL leases and defaults to concurrency four. Catalog, activity, health, audit retention, and provider-specific schedules are reconciled automatically for supported enabled instances. Watch checkpoint age, terminal jobs, provider error ratio, database load, and lease expiry. Raise concurrency only after measuring both database and upstream headroom.

Broad searches are durable operations with a preview, exact scope, dry-run option, idempotency key, independent target results, and cooperative cancellation. Do not retry with a new idempotency key merely because the browser lost the response; fetch the existing operation first.

Notification implementations are source-level outbound adapters in v1; there is no administrator routing UI or automatic delivery schedule yet. Recyclarr is likewise a local CLI boundary, not an HTTP instance, and is not bundled in the baseline image. Do not claim either is automatically configured.

## Audit, diagnostics, and privacy

Global `audit.read` can read authentication/system records and must remain tightly held. Diagnostics exports use a strict allowlist and pseudonymous references, but still disclose operational counts and policy state. Generate them only for a specific incident, transfer them securely, and delete them under the support retention policy.

Default logs must not include credentials, media paths/titles, provider bodies, or cookie/token material. Treat database dumps, the Data Protection ring, master keys, audit actor/IP data, and health source messages as sensitive.

## Backup, upgrade, and recovery

For every upgrade: stop app writers, create and restore-test the complete backup set, pull a pinned target digest, run target migrations, start, verify readiness/login/freshness, and keep rollback media immutable. Rollback is restore of the pre-upgrade database and keys plus the previous image—not down migrations.

If the active credential key is lost, restore the exact versioned key; replacing bytes under the same version cannot decrypt existing rows. If OIDC is unavailable, use the local administrator. If all authorization managers are lost through external database changes, stop the app and restore a known-good database rather than editing role rows ad hoc.
