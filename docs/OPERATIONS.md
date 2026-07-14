# Deployment, Operations, and CI/CD

## Images and tags

GitHub Actions builds `ghcr.io/nbt4/arrcontrol`. Pull requests build/test without publishing. `main` publishes `edge`; semantic tags publish the exact version, major/minor aliases, and `latest` only for stable releases. Images target linux/amd64 and linux/arm64, run non-root, include OCI labels, SBOM, provenance, and signatures.

## Compose lifecycle

Copy `.env.example`, rotate secrets, then `docker compose up -d`. Pin a version in production (`ARRCONTROL_TAG=0.1.0`), not `latest`. Back up PostgreSQL with `pg_dump --format=custom`; also retain the encryption master key separately. Restore into an equal/newer supported database, run migrations, then start the app.

## Reverse proxy

Terminate TLS at Caddy, Traefik, or nginx. Preserve WebSocket/SSE upgrades and request IDs. Configure trusted proxy networks; do not trust arbitrary forwarded headers. Public URL and OIDC redirect origin must match exactly.

## Observability

`/health/live` checks process liveness; `/health/ready` checks PostgreSQL and required startup state. OpenTelemetry OTLP export is optional. Logs are JSON in production. Recommended alerts: readiness failure, poll success age, job backlog, provider error ratio, database saturation, and repeated authentication failures.

## Release gates

Backend/frontend unit tests, provider contract tests, API compatibility check, migrations test, container smoke test, dependency/license scan, CodeQL, Trivy, secret scan, SBOM, signed image, changelog, upgrade/rollback notes. Database rollback is restore/forward-fix, not down migrations.
