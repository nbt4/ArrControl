# User Guide

## Sign in and preferences

Open ArrControl through its HTTPS address. Sign in with the local credentials supplied by an administrator or choose the configured Authentik/OIDC flow. Do not enter credentials on the direct HTTP listener (port `8080` by default); secure browser sessions require HTTPS.

Use the language selector for English or German. Anonymous choices stay in that browser. After sign-in, your saved language and timezone take precedence and timestamps render in that timezone. If the locale changes but a timestamp seems wrong, verify the saved timezone rather than converting the displayed text manually.

Sign out when using a shared browser. A refresh-token replay revokes the whole session family; if you are unexpectedly signed out, authenticate again and tell an administrator if it repeats.

## Reading the dashboard

- Overview totals come from ArrControl's local projections, not invented sample data.
- “Fresh” means the relevant source was observed within its expected window; “stale” means the last successful poll is old.
- Queue/history summarize current download and recent provider activity visible to your role.
- Health shows grouped incidents, severity text, affected source details, and safe remediation links.
- Audit is visible only with the global audit permission and contains redacted structured changes.
- Disabled navigation indicates a page/capability is not delivered or not available; it does not imply an upstream service is broken.

Permissions can be global or limited to instance groups. Seeing fewer instances or no mutation action may be intentional. Ask an administrator for the smallest necessary permission rather than a global role.

## Keyboard and accessibility

The first Tab reveals “Skip to content.” All delivered controls, login, language selection, incident sources, and audit disclosures work without a pointer. Focus has a visible outline; native browser zoom, forced colors, and reduced motion are supported. Report the browser/assistive-technology version, page/state, and exact keyboard sequence for an accessibility issue without including private media data.

## Getting help safely

Share only ArrControl version/image digest, browser version, timestamp/timezone, stable error code, and a redacted reproduction. Never post passwords, API keys, webhook/bot URLs, cookies, OIDC tokens, instance URLs, download IDs, media titles, paths, database dumps, master keys, or diagnostics exports publicly. Security issues use the private process in the root `SECURITY.md`.
