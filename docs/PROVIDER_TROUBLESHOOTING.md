# Provider Troubleshooting Matrix

Start with the stable ArrControl outcome, not an upstream response body. Use a read-only probe unless a documented operation was explicitly approved. Never log or paste configured URLs, credential values, cookies, tokens, download IDs, media titles, paths, or notification destinations.

## Stable outcomes

| Outcome | Meaning | Safe checks | Do not do |
| --- | --- | --- | --- |
| `credential_missing` | Required write-only purpose is not configured/decryptable. | Compare configured-purpose flags with `PROVIDERS.md`; verify every referenced master-key version is mounted. Replace the credential through the API if authorized. | Do not inspect ciphertext manually, reuse a key version with new bytes, or put a secret in the URL. |
| `unauthorized` | Upstream rejected authentication. | Confirm account/key remains active and has the documented provider permission; replace the secret and probe once. | Do not repeatedly retry, log headers/query strings, or broaden permission blindly. |
| `forbidden` | Authentication arrived but the upstream denied this resource/action. | Compare provider role/API permissions with the exact capability; use least privilege. | Do not switch off ArrControl RBAC or TLS. |
| `rate_limited` | Upstream returned throttling metadata. | Honor bounded Retry-After, reduce poll/operation pressure, inspect other clients using the same account. | Do not loop probes/searches or create new idempotency keys. |
| `unsupported_version` | Product/major version is outside contract evidence. | Compare status version with `PROVIDERS.md`; upgrade/downgrade to a tested range or contribute redacted official fixtures. | Do not force the kind/version or call it supported based on similar JSON. |
| `invalid_response` | Required documented shape/value was absent or inconsistent. | Verify correct base URL/product, check upstream health, and reproduce with a redacted fixture. | Do not persist/return the raw body or guess unknown fields. |
| `timeout` | Bounded call did not complete. | Check DNS, routing, provider load, database/source freshness, and whether the endpoint is unusually large. | Do not remove bounds or retry continuously. |
| `tls_error` | Certificate/handshake validation failed. | Correct hostname, chain, validity, reverse proxy, or trusted CA at the deployment boundary. | Do not silently disable verification or use an IP URL to evade hostname checks. |
| `unreachable` | DNS resolution or network connection failed safely. | Resolve from the ArrControl host, inspect firewall/routing, private-network opt-in, and full DNS answer set. | Do not allow loopback/metadata/reserved targets, redirects, or environment proxies. |
| `connected` | A generic unsupported-kind transport returned HTTP. | Treat only as network evidence and select a contract-supported adapter before expecting capabilities. | Do not advertise provider support from this result. |

## Provider families

| Family | Required credentials / base URL | Common contract checks | Family-specific notes |
| --- | --- | --- | --- |
| Sonarr/Radarr/Lidarr/Readarr/Whisparr | `api-key`; server root including any intentional base path | Exact product and tested major; `/api/v3` for Sonarr/Radarr/Whisparr, `/api/v1` for Lidarr/Readarr | A partial/oversized catalog rejects the whole snapshot and retains old data. Search payloads are bounded IDs only. |
| Prowlarr/Bazarr | `api-key`; server root/base path | Prowlarr major 2, Bazarr major 1 | Prowlarr discards indexer config/query data. Bazarr hashes object/path-like health fields and reports daily subtitle counts only. |
| SABnzbd | `api-key`; server root | Major 4/5 | Official API requires the key in a query; ArrControl redacts all request query rendering. Retry applies only to documented failed jobs. |
| NZBGet | `username` + `password`; RPC root | Major 25/26, Basic auth | Verify RPC account permissions and server clock; do not confuse web UI and RPC credentials. |
| qBittorrent | `username` + `password`; Web UI root | Major 5, login cookie | Reverse-proxy base path/cookie behavior must remain same-origin; ArrControl does not retain the cookie beyond the call sequence. |
| Transmission | `username` + `password`; RPC URL/root configured as documented | Major 4, 409 session header challenge | A bounded `X-Transmission-Session-Id` is expected; repeated 409 after retry indicates proxy/session mismatch. |
| Deluge | Web `password`; Web JSON-RPC root | Major 2 | Use the Deluge Web password, not a daemon auth-file line. |
| Plex | `api-key`; server root | Plex 1.41–1.43 and aggregate endpoints | Plex token is a header. ArrControl returns aggregate library/session counts, not users/titles/paths. |
| Jellyfin | `api-key`; server root | 10.10/10.11 and product identity | Ensure the API key can read system/library/session aggregates. |
| Emby | `api-key`; server root, without manually duplicating `/emby` | 4.8/4.9 and product identity | Adapter appends the documented `/emby` API prefix. |
| Overseerr/Jellyseerr/Seerr | `api-key`; server root | Overseerr 1.34/1.35; Jellyseerr 2.7/Seerr 3.0–3.3 | Health validates bounded request inventory; request titles/users/reasons/paths are deliberately discarded. |
| Ombi | `api-key`; server root | 4.47/4.48, separate movie/TV inventories | Header name is the upstream-defined `ApiKey`; do not rename it based on other providers. |
| Recyclarr | No HTTP instance; trusted executable/config absolute paths | CLI major 7/8, `--version` before sync | Baseline image does not bundle it. Preview first; non-preview mutates Sonarr/Radarr configuration. Never pass free-form shell text. |

## Notifications

Notification adapters have no automatic v1 routing UI. A contract test or explicit caller may deliver; registration alone sends nothing.

| Destination | Check | Sensitive material |
| --- | --- | --- |
| Generic webhook | HTTPS/private-network policy, receiver HMAC-SHA-256 verification, bounded 2xx | Endpoint and signing secret |
| Discord/Slack/Teams | Current incoming webhook type and provider status; Teams must be Workflows, not legacy connectors | Full webhook URL/path token |
| Telegram | Bot token path plus target chat ID; Bot API `sendMessage` result | Bot token and chat ID |
| ntfy/Gotify | Correct self-hosted root/topic/application endpoint and explicit private-network allowance | Bearer/application token and endpoint |
| Pushover | Both application and user keys; status response | Application/user keys |
| Email | `smtps://` implicit TLS, valid hostname certificate, SMTP AUTH PLAIN support inside TLS | Endpoint, sender/recipient, username/password |

For a repeatable adapter defect, contribute the smallest official-version fixture with every secret, URL, user, title, path, address, token, and unique download identifier replaced. Preserve structural types and status fields so the contract remains meaningful.
