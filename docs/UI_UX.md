# UI/UX Specification

## Design language: Quiet Signal

ArrControl uses deep graphite surfaces, mineral-gray typography, and a sea-glass green accent. Amber and coral are reserved for warning and failure. The product should feel operational and calm: dense enough for experts, legible enough for first-time users, never styled after Tsunami Events or any upstream Arr product.

## Information architecture

Primary navigation: Overview, Library, Missing, Queue, Imports, Search, Calendar, Health, Tasks, Statistics, Audit, Settings. Mobile collapses this into a bottom/action drawer. A global command/search surface is keyboard accessible.

## Page contracts

- Overview: freshness banner, status counts, queue throughput, missing by instance group, incidents, recent operations.
- Missing: server-side table, saved filters, source badges, monitored/quality/language filters, selection basket, previewed bulk search.
- Queue/Imports: correlated rows, progress, ETA, provider/client, diagnostic drawer, capability-aware actions.
- Health: incident groups, affected instances, first/last seen, source details, remediation links, acknowledge/snooze.
- Search: canonical title search, target instances, release comparison, rejection explanations, explicit grab confirmation.
- Settings: onboarding wizard, connection test, capability report, secret replacement, local/OIDC auth, roles, schedules, retention.

## Interaction rules

Every mutation reports pending/succeeded/partial/failed with an operation ID. Bulk actions show target count and scope before execution. Destructive actions require typed or contextual confirmation according to risk. Cached data always displays its observation time. Empty, loading, stale, permission-denied, partial, and upstream-offline states are specified for every data view.

## Localization

Translation keys are semantic (`queue.status.downloading`), never English source strings. No concatenated sentences. Layout tolerates 35% text expansion. Dates use `Intl`, bytes use IEC/SI per preference, and locale switching is immediate. English is fallback; German ships at parity.

## Accessibility and responsive behavior

WCAG 2.2 AA: visible focus, keyboard tables/actions, 44px touch targets, semantic landmarks, reduced-motion support, non-color status cues, and contrast-tested tokens. Desktop tables become prioritized cards below 720px; filters use a drawer without losing state.
