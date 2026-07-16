# Operator Guide

ArrControl works from timestamped PostgreSQL projections, so routine reads remain available when an upstream service is offline. Always distinguish “last known state” from a live upstream result.

## Dashboard and freshness

The v1 browser dashboard provides authentication, locale/timezone preferences, overview counts, queue/history summaries, health incidents, and audit details when permitted. Navigation entries without a delivered page are visibly disabled; use only documented API operations for capabilities not yet exposed in the browser.

Each projection shows observation/freshness state. Catalog data becomes stale after 30 minutes without a successful checkpoint; queue/history after two minutes. A stale result is not automatically wrong, but broad action decisions should wait for a successful poll or be explicitly reviewed against the upstream.

## Missing and search

Missing reads are cursor-paginated and can filter by instance, supported media kind, reason, and literal case-insensitive title. Saved views belong to the current user. `missing` means currently eligible without a file; `not_available` represents monitored future availability.

Before a search, preview one of four exact scopes: selected media IDs, instances, groups, or all. Review included/excluded counts and per-instance targets. Use dry-run for broad changes. The start call recomputes authorization/eligibility, snapshots targets, and needs an idempotency key. A partial outcome means some targets succeeded and others carry stable errors; it is not a total rollback.

Cancellation prevents new work where possible but cannot undo a provider request already accepted. Provider rate limits and the minimum per-instance spacing intentionally make a large operation take time.

## Queue, history, and import triage

Queue and history aggregate only visible instances. Correlation is same-instance and based on normalized download identifiers. Treat “correlated” as evidence of a relationship, not proof that filesystem import succeeded.

Import guidance is deterministic from normalized status/event fields. Unknown combinations remain unclassified. v1 never enables retry-import from a guessed rule; follow the guidance and safe upstream link instead. Do not paste raw download paths, release names, or provider response bodies into public issues.

## Health incidents

Incidents group related sources but preserve each source and first/last observation. Acknowledge means a human has seen the active incident; snooze hides attention until a bounded future time; neither repairs the provider. An empty successful health snapshot resolves active incidents, while a failed poll retains prior state.

Open remediation links as untrusted external pages. Verify the hostname before entering credentials. If the incident recurs, ArrControl reopens the stable group and clears stale acknowledgement/snooze state.

## Safe incident workflow

1. Record the ArrControl correlation/operation ID and timestamps, not credentials or raw provider payloads.
2. Check `/health/ready`, projection freshness, affected instances, and the stable provider/job error code.
3. Determine whether impact is ArrControl-wide, one instance/group, DNS/TLS/authentication, upstream rate limiting, or database capacity.
4. Prefer read-only connection probes and provider status pages. Avoid repeated login/search/probe loops that worsen throttling.
5. Escalate with a strict diagnostics export only when authorized; send it through a private channel.
6. After remediation, confirm a successful checkpoint and resolved/updated incident rather than relying solely on a green upstream UI.

The provider-specific decision table is in `PROVIDER_TROUBLESHOOTING.md`.
