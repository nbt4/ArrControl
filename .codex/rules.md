# Codex Rules

1. Start by inspecting repository state and relevant documentation; do not overwrite user changes.
2. Implement the earliest unblocked TODO item as a tested vertical slice.
3. Domain has no infrastructure dependencies. Application depends only on Domain. Infrastructure implements Application contracts. API is composition/UI boundary.
4. Controllers/endpoints validate transport concerns only; business rules live in application/domain code.
5. Every mutation has authorization, validation, audit behavior, typed errors, cancellation, and idempotency analysis.
6. Secrets are write-only, encrypted, redacted, and absent from fixtures/snapshots/logs.
7. Provider-specific DTOs never escape provider packages. Unknown upstream values must not crash sync.
8. Update OpenAPI before/with API behavior and regenerate clients reproducibly.
9. All user-facing text is localized; English and German catalogs remain in parity.
10. Add proportionate unit/integration/contract/E2E tests. Run the narrowest tests first, then the full affected suite.
11. Do not introduce Redis, message brokers, microservices, runtime plugin loading, or new frameworks without an ADR and measured requirement.
12. Report assumptions, commands/tests run, remaining risks, and exact files changed.
