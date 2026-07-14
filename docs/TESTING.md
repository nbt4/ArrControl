# Test Strategy

- Unit: domain invariants, authorization policies, normalization, scheduling, error mapping.
- Architecture: module/reference boundaries and absence of provider DTO leakage.
- Integration: PostgreSQL through Testcontainers, migrations, outbox, leases, idempotency.
- Provider contract: redacted fixtures across supported versions plus optional live smoke tests.
- API: OpenAPI conformance, RFC 9457 errors, permission matrix, pagination stability.
- Frontend: component states, accessibility, localization parity, generated client contract.
- End-to-end: bootstrap, local login, Authentik test realm, onboarding, search dry-run, partial bulk failure.
- Security: SAST, dependency/container scans, secret scan, SSRF cases, auth rate limits.
- Performance: 100k catalog items, 20 instances, 10k-target bulk operation, reconnect storm.

Coverage is diagnostic, not a target substitute. Changed critical application/domain code should normally exceed 80% branch coverage. Release candidates require a clean install and upgrade from the previous stable version.
