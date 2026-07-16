# Real Authentik OIDC integration tests

This opt-in project starts an isolated, disposable identity-provider stack with
Testcontainers:

- `ghcr.io/goauthentik/server:2026.5.4` as both server and worker;
- `docker.io/library/postgres:16.10-alpine` as Authentik's authoritative store;
- a random PostgreSQL password, Authentik secret key, bootstrap API token, OIDC
  client credentials, and test-user password for every fixture run.

It does not contact or mutate an existing Authentik installation. The worker
applies `Fixtures/arrcontrol-e2e-blueprint.yaml`; the bootstrap token is used
only to verify the resulting provider, application, scope mapping, and user via
Authentik's own API. The worker intentionally has no Docker-socket mount because
this realm does not create outposts.

The project is part of `ArrControl.slnx`, so ordinary solution builds compile
and discover it, but all four tests remain skipped unless an explicit opt-in
variable is set. Run the two real container contract tests with:

```bash
ARRCONTROL_RUN_AUTHENTIK_TESTS=1 \
  dotnet test tests/ArrControl.AuthentikIntegrationTests/ArrControl.AuthentikIntegrationTests.csproj \
  --filter FullyQualifiedName~AuthentikContractTests
```

For both browser flows, build once and install Playwright's pinned Chromium
runtime, then enable the browser tests:

```bash
dotnet build tests/ArrControl.AuthentikIntegrationTests/ArrControl.AuthentikIntegrationTests.csproj \
  --configuration Release
pwsh tests/ArrControl.AuthentikIntegrationTests/bin/Release/net9.0/playwright.ps1 \
  install --with-deps chromium
ARRCONTROL_RUN_AUTHENTIK_E2E=1 \
  dotnet test tests/ArrControl.AuthentikIntegrationTests/ArrControl.AuthentikIntegrationTests.csproj \
  --configuration Release
```

The provider-focused browser test signs in through Authentik's real default
flow. It proves that an authorization code bound to an S256 challenge rejects a
wrong verifier, redeems a fresh code with the correct verifier, validates the
RS256 ID-token signature against the provider JWKS, and checks issuer, audience,
nonce, subject, and the test realm's explicitly verified email claim. It also
calls the real user-info endpoint.

The ArrControl end-to-end browser test runs the real API handler against the
same Authentik realm and a separate ephemeral PostgreSQL database. It verifies
the emitted S256 challenge, strict callback, real code exchange, verified-email
provisioning, protected opaque session, protected ID-token logout context, a
real Authentik group-to-Administrator assignment, local family revocation,
Authentik RP logout, and exact signed-out callback. No provider access or refresh
token is persisted.

## Test transport boundary

Authentik itself is exposed over isolated loopback HTTP by Testcontainers. The
ArrControl E2E keeps its registered application callbacks on the logical origin
`https://arrcontrol.test`; Playwright captures and verifies the provider-driven
browser navigations before Chromium attempts that deliberately non-routable
origin, and the test submits them to the in-memory API host. The test host
relaxes HTTPS metadata solely when connecting to the ephemeral container.
Production still requires HTTPS metadata and exact HTTPS redirect URIs. The
provider-focused flow uses a separate HTTP loopback callback.

The blueprint's `email_verified: True` mapping is a test fixture for a claim
backed by a verified source. Authentik's safe default is `False`; deployments
must not copy this mapping unless their source actually proves ownership.
