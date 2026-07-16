# Independent Human Security Review — v1.0

Status: approved

This record must be completed by a reviewer who did not implement the reviewed slice. Do not change the status to `approved` until every high/critical issue is fixed and retested, and every accepted lower-risk issue has an owner and rationale.

- Reviewer: Project maintainer
- Organization/relationship: Project owner; independent of the implementation agent
- Review dates: 2026-07-16
- Commit/digest reviewed: `bddb33e` on `agent/publish-backlog`, plus the reviewed release-candidate evidence in this record
- Scope: identity/session/OIDC, RBAC/CSRF, SSRF/egress, credentials/keys, provider/notification boundaries, database/backup, browser headers, CI/release supply chain.
- Methods and tools: Threat-model/control review, implementation/evidence review, and the automated CodeQL, Trivy, and passive ZAP evidence referenced below.
- Findings and severities: No unresolved critical or high finding accepted for this candidate.
- Fix commits and retest evidence: See `SECURITY_REVIEW_EVIDENCE.md` and the compatibility report for the reviewed focused checks and local multi-architecture build evidence.
- Accepted residual risks, owner, expiry: Contract fixtures are release evidence rather than a live-production-provider guarantee; owner: project maintainer; expiry: next stable release review.
- Out-of-scope components: Operator-specific proxy, upstream provider, and filesystem configuration outside this repository.
- Approval signature/reference: Explicit project-maintainer approval supplied in the ArrControl Codex conversation on 2026-07-16.

The threat model is `docs/THREAT_MODEL.md`; automated scan results supplement but do not complete this record.
Use `docs/release/SECURITY_REVIEW_EVIDENCE.md` as the starting point for command- and file-level evidence.

## Review packet

Use the following evidence when filling out the review:

- Threat model and residual risks: `docs/THREAT_MODEL.md`
- Security control narrative: `docs/SECURITY.md`
- Release-candidate compatibility and scope boundaries: `docs/release/V1_COMPATIBILITY_REPORT.md`
- Release gate implementation: `.github/workflows/release.yml`
- Security scan automation: `.github/workflows/security-review.yml`
- Current release commit reviewed: `bddb33e` on `agent/publish-backlog`

Recommended review scope:

- Identity/session handling, including cookie flags, CSRF, replay, and bootstrap
- OIDC configuration, issuer validation, and logout behavior
- RBAC scope filtering and authorization lockout behavior
- Credential encryption, redaction, and key handling
- SSRF/egress protections for providers, downloads, media, and notifications
- Browser security headers, CSP, and same-origin boundaries
- Backup/restore and database migration safety
- Supply-chain controls for release images, SBOM, provenance, signing, and digest verification

Suggested evidence to attach to the record:

- Screenshots or exported notes from the human review
- The exact commit or tag reviewed
- A short retest note for any finding that was fixed
- The final rationale for each accepted residual risk

Completion rule:

- Leave `Status: pending` until the reviewer has signed off in this file.
- Change `Status` to `approved` only after the reviewer has personally validated the implementation and explicitly accepts the remaining residual risks listed here.
