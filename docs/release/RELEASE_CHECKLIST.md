# v1.0 Release Checklist

- [ ] Freeze and review the exact commit; confirm `VersionPrefix` and tag are `1.0.0` / `v1.0.0`.
- [ ] Confirm every `TODO.md` item is complete or explicitly release-blocking.
- [ ] Obtain `Status: approved` in `SECURITY_REVIEW.md` from an independent reviewer.
- [ ] Run required CI, accessibility/manual RC checks, provider contracts, data lifecycle, capacity, CodeQL, Trivy, ZAP, API/migration drift, and image smoke.
- [ ] Optionally run read-only live provider smoke against isolated non-production fixtures; record endpoints only as pseudonymous labels.
- [ ] Restore-test the release backup procedure and write upgrade/rollback notes.
- [ ] Confirm compatibility report and known limitations match delivered UI/API behavior.
- [ ] Obtain explicit maintainer approval to tag/push/publish.
- [ ] Push `v1.0.0`; require the release workflow to publish one amd64/arm64 manifest digest, SBOM/provenance attestations, a passing scan, and verified Cosign identity.
- [ ] Independently pull by digest, verify signature, smoke both supported architectures, and attach the workflow evidence to release notes.
- [ ] Announce only the immutable digest and supported contract matrix; monitor readiness, migrations, auth failures, jobs, and advisories.
