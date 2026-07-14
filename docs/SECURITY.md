# Security and Authentication

## Local identity

Bootstrap creates one admin only when the user table is empty and required environment variables are present; the password is never logged and bootstrap is then disabled. Passwords use Argon2id with parameters stored per hash. Login has IP/account throttling, generic errors, audit events, optional TOTP/WebAuthn roadmap, and rotating refresh tokens stored as hashes. Access tokens are short-lived and delivered using secure, HttpOnly, SameSite cookies for the first-party UI.

## OIDC / Authentik

Use Authorization Code with PKCE, exact redirect URIs, issuer discovery, state/nonce validation, and signature/key rotation. Required claims: `iss`, `sub`; email linking requires `email_verified=true`. Groups map to roles through explicit configuration. Local login can be disabled only after a tested OIDC admin mapping and recovery procedure.

Authentik reference redirect: `https://arrcontrol.example.com/auth/oidc/callback`; post-logout redirect must be allowlisted. Reverse proxies must pass trusted forwarded headers, with known proxy networks configured.

## Authorization permissions

Examples: `instances.read`, `instances.manage`, `library.read`, `search.execute`, `queue.manage`, `tasks.execute`, `users.manage`, `audit.read`, `settings.manage`. Permissions are checked in application commands and filtered in queries; hiding a button is not authorization.

## Secrets and transport

Provider/API credentials use AES-256-GCM envelope encryption with a 32-byte master key mounted as a secret. Ciphertext, nonce, tag, and key version live in PostgreSQL. API responses return only `configured: true`. TLS verification defaults on. CSP, HSTS, anti-forgery, secure headers, request-size limits, and dependency scanning are release gates.

## Threat considerations

SSRF is constrained by validating schemes, resolving/rechecking addresses, blocking cloud metadata/link-local destinations by default, and an explicit private-network policy because homelabs require RFC1918 access. URL redirects are disabled or revalidated. Logs redact authorization headers, API keys, cookies, query secrets, filesystem paths, and media names in support bundles unless opted in.

Security reports go through GitHub private vulnerability reporting. Never open a public issue containing a live credential or exploit details.
