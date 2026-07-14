# Assumptions and Open Decisions

1. Backend is ASP.NET Core 9 and frontend React 19/TypeScript; this follows the prior project defaults.
2. A modular monolith is the right initial operational tradeoff; provider isolation remains an evolution path.
3. “All common services” is a roadmap commitment through the documented compatibility matrix, not simultaneous full implementation in the initial scaffold.
4. PostgreSQL 17 is the baseline; Redis and Hangfire are intentionally absent until measured need.
5. English and German ship first; architecture supports more locales.
6. MIT best matches “open source and free for everyone” and permits personal, commercial, modified, and redistributed use with attribution. The owner should confirm this legal choice before the first release.
7. GitHub Container Registry is the canonical image registry and GitHub Actions is CI/CD.
8. The first deployment serves SPA and API from one image on port 8080.
9. No external telemetry is enabled by default.
10. Automated changes to upstream media/filesystems are conservative and capability-gated.

Open decisions before v0.1 freeze: project logo/domain, public support channels, maintainer security contact, exact browser support, semantic versioning policy pre-1.0, and whether local login may be globally disabled.
