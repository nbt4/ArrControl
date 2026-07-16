# Security Review Evidence Map

This file is a reviewer aid for the pending human security review in `docs/release/SECURITY_REVIEW.md`.

It does not replace the required independent signoff.

## Scope map

| Review area | Primary evidence |
| --- | --- |
| Identity/session/bootstrap | `docs/SECURITY.md`, `src/ArrControl.Api/Identity/*`, `tests/ArrControl.IntegrationTests/LocalAuthenticationApiTests.cs`, `tests/ArrControl.UnitTests/LocalIdentityServiceTests.cs`, `tests/ArrControl.UnitTests/SecureSessionTokenServiceTests.cs` |
| OIDC/Authentik | `docs/SECURITY.md`, `src/ArrControl.Api/Identity/OidcAuthenticationApi.cs`, `tests/ArrControl.AuthentikIntegrationTests/*`, `tests/ArrControl.IntegrationTests/OidcAuthenticationApiTests.cs` |
| RBAC/CSRF | `docs/SECURITY.md`, `src/ArrControl.Api/Authorization/*`, `tests/ArrControl.IntegrationTests/RbacAuthorizationApiTests.cs`, `tests/ArrControl.UnitTests/RbacAuthorizationServiceTests.cs` |
| Credentials/keys | `docs/SECURITY.md`, `src/ArrControl.Infrastructure/Connections/*`, `tests/ArrControl.UnitTests/AesGcmCredentialProtectorTests.cs` |
| SSRF/egress | `docs/SECURITY.md`, `src/ArrControl.Infrastructure/Providers/*`, `tests/ArrControl.UnitTests/OutboundTargetPolicyTests.cs`, `tests/ArrControl.UnitTests/ArrProviderClientContractTests.cs` |
| Database/backup | `docs/BACKUP_RESTORE.md`, `docs/THREAT_MODEL.md`, `tests/data-lifecycle/run.sh`, `tests/ArrControl.IntegrationTests/DatabaseMigrationCommandTests.cs` |
| Browser headers | `src/ArrControl.Api/Program.cs`, `tests/ArrControl.IntegrationTests/SecurityHeaderApiTests.cs`, `docs/THREAT_MODEL.md` |
| CI/release supply chain | `.github/workflows/security-review.yml`, `.github/workflows/release.yml`, `docs/release/V1_COMPATIBILITY_REPORT.md` |

## Quick anchors

- `docs/release/SECURITY_REVIEW.md:3` keeps the review pending until a real independent signoff exists.
- `docs/release/SECURITY_REVIEW.md:22` starts the review packet; `docs/release/SECURITY_REVIEW.md:31` pins the current reviewed commit.
- `docs/release/SECURITY_REVIEW.md:51` and `docs/release/SECURITY_REVIEW.md:53` define the completion rule.
- `docs/release/V1_COMPATIBILITY_REPORT.md:15` records the local amd64/arm64 build evidence; `docs/release/V1_COMPATIBILITY_REPORT.md:54` keeps the human review pending.
- `.github/workflows/security-review.yml:26`, `:34`, and `:66` show the automated ZAP review steps and artifact retention.
- `.github/workflows/release.yml:33`, `:82`, `:97`, and `:118` show the release gate, signing, manifest verification, and evidence capture.
- `docs/SECURITY.md:7`, `:19`, `:25`, `:27`, `:53`, `:55`, `:65`, `:69`, `:77`, `:87`, `:89`, `:93`, `:97`, `:99`, and `:101` contain the control narrative the reviewer should test against.

## High-signal validation commands

The following commands were already run in this workspace and are the most relevant starting point for a fresh human review:

```bash
dotnet build ArrControl.slnx --no-restore -p:TreatWarningsAsErrors=true
```

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/ArrControl.IntegrationTests/ArrControl.IntegrationTests.csproj \
  --no-restore --filter "FullyQualifiedName~SecurityHeaderApiTests|FullyQualifiedName~RbacAuthorizationApiTests|FullyQualifiedName~OidcAuthenticationApiTests"
```

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/ArrControl.UnitTests/ArrControl.UnitTests.csproj \
  --no-restore --filter "FullyQualifiedName~AesGcmCredentialProtectorTests|FullyQualifiedName~OutboundTargetPolicyTests|FullyQualifiedName~SecureSessionTokenServiceTests|FullyQualifiedName~RbacAuthorizationServiceTests|FullyQualifiedName~LocalIdentityServiceTests"
```

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/ArrControl.AuthentikIntegrationTests/ArrControl.AuthentikIntegrationTests.csproj --no-restore
```

## Release evidence

- Release candidate report: `docs/release/V1_COMPATIBILITY_REPORT.md`
- Human review record: `docs/release/SECURITY_REVIEW.md`
- Release workflow: `.github/workflows/release.yml`
- Local multi-architecture build evidence: manifest list in `artifacts/release/arrcontrol-v1.0.0.oci`

## Observed results in this workspace

These were already exercised in the current workspace before the review packet was assembled:

- Solution build with warnings-as-errors passed.
- Focused security-header, RBAC, and OIDC integration checks passed.
- Focused unit tests for credential protection, outbound policy, session tokens, local identity, and RBAC service passed.
- Local passive ZAP baseline returned zero failures.
- Local OCI build for the release container succeeded for both `linux/amd64` and `linux/arm64`.

## Reviewer notes

- Keep `Status: pending` until an independent reviewer completes the form in `docs/release/SECURITY_REVIEW.md`.
- Record the reviewed commit or tag exactly.
- If a finding is fixed, include the fix commit and retest evidence in the review record.
- The accepted risk section should state the owner and expiry for every non-blocking issue.
