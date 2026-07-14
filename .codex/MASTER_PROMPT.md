# ArrControl Master Prompt for Codex

You are implementing ArrControl in this repository.

First read, in order: `AGENTS.md`, `.codex/rules.md`, `README.md`, `docs/ASSUMPTIONS.md`, `docs/SRS.md`, `docs/SDD.md`, `docs/DATA_MODEL.md`, `docs/PROVIDERS.md`, `docs/SECURITY.md`, `docs/api/openapi.yaml`, `docs/UI_UX.md`, `docs/OPERATIONS.md`, `docs/TESTING.md`, `docs/ROADMAP.md`, and `TODO.md`. Then inspect the actual repository and git status; documentation is intent, code and tests reveal the current state.

Implement the earliest unblocked P0 task in `TODO.md` as a complete vertical slice. If it is too large for one safe change, split it into independently verifiable subtasks in `TODO.md` and complete the first one. Do not merely scaffold or describe work when implementation is possible.

Requirements:

- Follow the modular boundaries and security rules exactly.
- Keep API/OpenAPI, database migrations, generated client, UI localization, documentation, and tests consistent.
- Never expose credentials, weaken TLS silently, or perform unapproved mutations against real services.
- Use capability-driven provider behavior and contract fixtures; do not pretend roadmap providers are implemented.
- Preserve user changes and avoid unrelated refactors.
- Run formatting, builds, and affected tests. If tooling is unavailable, state the exact limitation and perform static verification.
- Mark a TODO complete only when its acceptance behavior and tests are genuinely complete.

At the end, summarize the user-visible outcome, architecture/security choices made, tests and commands run, remaining risks, and the next backlog item. Continue with the next item only if the user asked for autonomous multi-item execution.
