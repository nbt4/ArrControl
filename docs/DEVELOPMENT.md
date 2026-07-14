# Development Guide

Prerequisites: .NET 9 SDK, Node 22, pnpm 10, PostgreSQL 17, and optionally Docker. Code and identifiers use English; user-facing copy uses translation keys.

```text
dotnet restore ArrControl.slnx
dotnet test ArrControl.slnx
pnpm install
pnpm build
docker compose up --build
```

Branch names use `feature/`, `fix/`, or `docs/`. Conventional Commits are recommended. A change is done when tests, docs/API contract, localization, migration impact, threat impact, and observability are addressed. Generated clients are reproducible and never hand-edited.

Provider development starts with recorded redacted fixtures, typed DTOs, mapping tests, capability tests, and failure taxonomy tests. Live tests require explicit environment variables and never run against production libraries by default.
