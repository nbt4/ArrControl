# Security Policy

## Reporting a vulnerability

Use GitHub private vulnerability reporting for this repository. Do not open a public issue or discussion containing exploit details, live URLs, credentials, cookies, tokens, media metadata, or backup material. If private reporting is unavailable, contact the repository owner through a private channel listed on their GitHub profile and disclose only enough to establish a secure reporting route.

Include affected version/digest, impact, prerequisites, a minimal reproduction against an isolated test deployment, and any known mitigation. Remove all real secrets and personal/media data. Maintainers should acknowledge the report privately, preserve evidence, assign severity, coordinate a fix and disclosure date, and credit the reporter if requested.

## Supported versions

Before v1.0, only the current `main` development line receives security fixes. After v1.0, the latest stable minor line is supported; older images should be upgraded unless a release advisory explicitly says otherwise. Verify release image signatures as documented in `docs/OPERATIONS.md`.

The implementation threat model and review status are in `docs/THREAT_MODEL.md`.
