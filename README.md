# ArrControl

ArrControl is a free, open-source operations center for multiple Arr instances, download clients, and media servers. It unifies missing media, queues, health, searches, scheduled jobs, and audit history without embedding upstream UIs.

This repository is an implementation-ready blueprint **and** a runnable vertical slice. The initial slice exposes health/instance APIs and a localized dashboard; the provider contracts, schema, API contract, delivery pipeline, and backlog define the path to the complete product.

## Quick start

1. Copy `.env.example` to `.env` and change every placeholder secret.
2. Run `docker compose up --build`.
3. Open `http://localhost:8080` (API: `http://localhost:8080/api/v1/system/status`).

For local development, use .NET 9 SDK and Node 22/pnpm 10. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Documentation map

- [Product requirements](docs/SRS.md)
- [Architecture and decisions](docs/SDD.md)
- [Data model](docs/DATA_MODEL.md)
- [Provider architecture](docs/PROVIDERS.md)
- [API contract](docs/api/openapi.yaml)
- [UI system](docs/UI_UX.md)
- [Security and authentication](docs/SECURITY.md)
- [Operations and delivery](docs/OPERATIONS.md)
- [Roadmap](docs/ROADMAP.md) and [implementation backlog](TODO.md)
- [Codex master prompt](.codex/MASTER_PROMPT.md)

## Scope statement

“Support all services” means a stable provider architecture plus the compatibility matrix in `docs/PROVIDERS.md`. Sonarr and Radarr are the reference providers for v0.1; other providers are delivered in roadmap waves and must pass contract tests before being marked supported.

## License

Licensed under the MIT License. Everyone may use, study, modify, and redistribute ArrControl free of charge under its terms. See [LICENSE](LICENSE).

