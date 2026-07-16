# Accessibility Audit

ArrControl targets WCAG 2.2 level AA for the browser interface. The v1 audit covers every currently rendered anonymous and authenticated dashboard state in English and German, at desktop and narrow responsive widths. Planned pages that are not rendered by the v1 dashboard are not represented as passing content.

## Audited baseline

- Standard: WCAG 2.2, levels A and AA.
- Browser baseline: Chromium through Playwright 1.61.1.
- Automated rules: axe-core 4.12.1 tags `wcag2a`, `wcag2aa`, `wcag21aa`, and `wcag22aa`.
- Keyboard paths: skip navigation, locale selection, local login, navigation, incident source disclosure, and audit-detail disclosure.
- States: anonymous login, authenticated overview, health incidents, audit timeline, loading, error, stale, permission-absent, and disabled navigation.
- Audit date: 2026-07-16.

The focused browser suite is `apps/web/e2e/accessibility.spec.ts`. Run it with `pnpm test:e2e`; install its browser once with `pnpm --filter @arrcontrol/web exec playwright install --with-deps chromium`.

## Results

The audited baseline has zero automated axe violations and both keyboard-only journeys pass. Source and browser inspection found the following controls in place:

| WCAG area | Evidence in the audited UI | Result |
| --- | --- | --- |
| 1.1.1 non-text content | The dashboard has no content images; decorative presentation is CSS-only. | Pass |
| 1.3.1/1.3.2 structure and sequence | Native header, nav, main, sections, lists, forms, labels, headings, and disclosure elements preserve reading order. | Pass |
| 1.4.1/1.4.3/1.4.11 color and contrast | Statuses include text, foreground/background tokens pass axe contrast checks, and focus indicators are solid high-contrast outlines. | Pass |
| 1.4.4/1.4.10 resize and reflow | Relative text sizing and the narrow card layout avoid two-dimensional page scrolling at supported widths. | Pass |
| 2.1.1/2.1.2 keyboard and traps | All active controls are native keyboard controls; the tested paths complete without a pointer or focus trap. | Pass |
| 2.4.1 bypass blocks | The first focusable control is a skip link that moves focus to the main region. | Pass |
| 2.4.3/2.4.7/2.4.11 focus | DOM order is logical, focus is visibly outlined, and no sticky overlay obscures focused content. | Pass |
| 2.5.7 dragging | No operation requires dragging. | Not applicable |
| 2.5.8 target size | Interactive targets have a minimum 44 px block size, exceeding the 24 CSS px AA minimum. | Pass |
| 3.1.1 language | The document language follows the active English or German locale. | Pass |
| 3.2.1/3.2.2 predictable input | Focus does not mutate state; locale changes are explicit and form submission is conventional. | Pass |
| 3.3.1/3.3.2 errors and labels | Login fields have persistent labels and failures are presented as text status. | Pass |
| 4.1.2 name, role, value | Native controls and disclosure widgets expose platform semantics; axe reports no naming violations. | Pass |

Reduced-motion preferences disable nonessential animation. Forced-colors mode retains platform colors and visible focus. Statuses never depend on color alone.

## Release gate and limitations

CI runs the two focused Chromium scenarios after lint and component tests. Any axe violation, broken tab order, inaccessible disclosure, or failed skip link blocks the build.

Automated analysis cannot prove complete conformance. Before each release candidate, manually repeat keyboard navigation at 200% zoom, narrow reflow, English/German text expansion, a screen-reader landmark/heading pass, forced-colors mode, and reduced-motion mode. Record browser, assistive-technology version, defects, and remediation in the release compatibility report. New pages and interactive states enter the audit scope when they become reachable; this report must not be used to claim that unimplemented pages were audited.
