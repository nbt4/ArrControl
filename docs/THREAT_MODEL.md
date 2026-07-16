# Threat Model and Security Review

This v1 model uses STRIDE over ArrControl's trust boundaries. It was reviewed against the implementation and tests on 2026-07-16. It complements the control-level detail in `docs/SECURITY.md`; it does not turn automated scanning into a claim of independent human penetration testing.

## Scope and assets

Protected assets are local/OIDC identities, opaque session material, provider and notification credentials, master/Data Protection keys, authorization assignments, instance topology, media/activity/health metadata, audit records, backups, operation intent/results, and the release supply chain. Availability of the API, PostgreSQL projections, and scheduler is also an asset.

Trust boundaries are: browser to TLS reverse proxy/API; API to PostgreSQL; API/worker to untrusted provider/DNS networks; OIDC browser/API to Authentik; application process to mounted keys/Recyclarr; CI runner to GitHub/GHCR and third-party actions; operator to backup storage. Provider payloads, browser input, forwarded headers, DNS answers, OIDC tokens, imported databases, and build dependencies are untrusted.

## STRIDE analysis

| Threat | Abuse path | Primary controls | Residual risk / owner action |
| --- | --- | --- | --- |
| Spoofing | Stolen/replayed cookies; forged OIDC token; spoofed forwarded IP | Hashed opaque tokens, bounded rotation/family replay revocation, Secure host cookies, CSRF, RS256 issuer/audience/nonce/PKCE validation, exact trusted proxies | No MFA/back-channel logout in v1; use short access lifetime, Authentik MFA, TLS, and monitor replay events. |
| Tampering | Credential-row relocation, operation replay, audit mutation, stale worker commit, modified image | AES-GCM AAD, idempotency hash/locks, append-only audit trigger, lease token fencing, signed immutable digest/SBOM/provenance | Database superuser and compromised CI identity remain high impact; separate duties and protect GitHub environments. |
| Repudiation | Broad command or role change denied by actor | Actor/correlation/IP/outcome audit, per-target operation results, immutable event history | Audit deletion follows retention and database admins can delete; export/retain according to policy. |
| Information disclosure | Secrets in URLs/logs/errors/backups/provider DTOs; RBAC enumeration | Write-only encrypted credentials, redacted objects/errors, bounded typed projections, SQL scope filtering, strict diagnostics allowlist, protected backups, CSP/referrer policy | Media titles and source messages remain sensitive to authorized readers; limit grants and log access. |
| Denial of service | Argon2 floods, provider payload amplification, 100k projection sort, reconnect storm, job lease exhaustion | Raw/durable auth limits, KDF admission, body/snapshot/page bounds, indexed keyset reads, 256 MiB DB shared memory, 50-concurrency reconnect gate, leased jobs/backoff/timeouts | One modular process and PostgreSQL are availability dependencies; alert, back up, and benchmark before exceeding the capacity envelope. |
| Elevation of privilege | Client-supplied groups/targets, stale OIDC/manual roles, ungrouped instance access, CSRF mutation | Current database authorization per request, explicit scope intersection, global-only admin/audit, OIDC-session identity binding, lockout guard, exact CSRF | Incorrect custom-role assignment is an administrator risk; retain a local recovery administrator and review audit changes. |

## Security invariants reviewed

- Credentials never appear in API reads, audit summaries, diagnostics, exception/object text, provider result models, or default logs.
- All mutations have authentication/authorization, validation, CSRF where cookie-authenticated, audit behavior, cancellation, and an explicit idempotency decision.
- Outbound HTTP re-resolves and pins approved addresses, rejects unsafe/mixed answers, disables redirects/proxies/cookies, retains original TLS host validation, and bounds time/body size.
- Provider and notification DTOs are allowlisted; unknown upstream values fail soft while unknown major contracts fail closed.
- Browser responses deny framing/object execution, constrain same-origin content/connect/form/resource sources, suppress referrers and sensitive browser capabilities, and emit HSTS only on HTTPS.
- Database schema changes are forward migrations; backup/restore and previous-version upgrade are isolated CI gates.
- Release jobs use commit-pinned actions, vulnerability/secret/config scanning, SBOM/provenance, digest signing, and signature verification.

## Automated independent review

CodeQL reviews C# and TypeScript data/control flow; Trivy independently reviews dependencies, image packages, secrets, and configuration; OWASP ZAP performs a passive dynamic baseline against the built non-root/read-only image; provider contract suites fuzz unknown fields/statuses at the trust boundary. High/critical Trivy findings, CodeQL alerts, ZAP failures, header regressions, or contract violations block their workflows. Scanner versions/images are immutable pins and reports are retained as CI artifacts where supported.

These tools are independent of the implementation but are not a substitute for a human review. Before declaring the first public GA release, a person who did not implement the reviewed slice must examine this model, authentication/authorization, SSRF/egress, credential lifecycle, backup/recovery, dependency pins, and release evidence; record reviewer, date, scope, findings, fixes, accepted risks, and retest in `docs/release/SECURITY_REVIEW.md`. Until that signed-off record exists, release status must say “automated security review complete; independent human review pending.”

## Review cadence

Revisit this model for every new trust boundary, authentication method, credential purpose, provider mutation, executable, public endpoint, proxy mode, persistence of raw data, replica topology, or release infrastructure change. Review it at least once per release candidate even when no boundary changed.
