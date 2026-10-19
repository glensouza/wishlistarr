# Authentication

Wishlistarr has two kinds of signed-in principal: a **Parent** and a **Kid**. They are separate roles checked by separate authorization policies, and a principal that carries both roles is rejected by both. Every endpoint under `/api` is either public, requires the Parent policy, or (the few Kid session routes) requires the Kid policy; a test walks the endpoint list to keep it that way and to confirm a Kid principal gets `403` on every parent route.

Public endpoints: `GET /api/health`, `GET /api/v1/auth/status`, `GET /api/v1/auth/antiforgery`, `POST /api/v1/auth/setup`, `POST /api/v1/auth/login`, `GET /api/v1/kid/profiles` (the profile picker) and `POST /api/v1/kid/signin`. Kid sessions and PIN limits are described in [kid-profiles.md](kid-profiles.md).

## First run

Until a parent account exists, `GET /api/v1/auth/status` reports `setupRequired: true`. Parent endpoints still answer `401` to callers with no credentials; callers that do authenticate before setup (the API key, the trusted-network bypass, or a Kid) get `409 Conflict` instead of reaching the endpoint. `POST /api/v1/auth/setup` with `{ "username", "password" }` creates the account and signs it in. It only works once; later calls return `409`.

Setup is first come, first served: until the parent creates the account, anyone who can reach Wishlistarr can claim it. Complete setup right after the first start, before exposing the app to other devices.

Usernames are 1 to 100 characters and compared case-insensitively. Passwords are 10 to 256 characters (longer login attempts are rejected before hashing) and stored with the ASP.NET Core Identity password hasher (PBKDF2 with HMAC-SHA512, salted).

## Parent sessions

`POST /api/v1/auth/login` starts a session and sets the `wishlistarr.auth` cookie (HttpOnly, `SameSite=Strict`). The cookie is marked `Secure` only when the request arrives over HTTPS; on a plain-HTTP LAN the password and cookie travel in clear text, so anyone who can watch the network can capture them. Put Wishlistarr behind HTTPS where that matters. Each session is a row in the database and the cookie is only accepted while that row exists and has not expired, so sessions can be ended server side.

- `GET /api/v1/auth/sessions` lists the signed-in parent's active sessions with their user agent, address and last use; `isCurrent` marks the caller's own.
- `DELETE /api/v1/auth/sessions/{id}` revokes one of the signed-in parent's sessions.
- `POST /api/v1/auth/logout` revokes the current session.

Both act only on the account behind the session cookie. API key and trusted-network callers have no account, so they see an empty list and get `404` on revoke.

Sessions last 30 days (`Auth:SessionLifetime`) from sign-in.

## Sign-in limits

- Each client address may make 10 setup or login requests per minute (`Auth:LoginAttemptsPerMinute`). Further requests get `429` with `Retry-After`. The limit is keyed on the client IP address, so behind a reverse proxy that is not listed in `KnownProxies` every client shares the proxy's address and one bucket.
- After 5 consecutive wrong passwords (`Auth:MaxFailedLogins`) the account is locked for 15 minutes (`Auth:LockoutDuration`). While locked, login returns `429` with `Retry-After`, even with the right password. A successful login resets the count.

Because the lockout is per account, someone who can reach the login page can keep the parent locked out. Use the API key or the trusted-network bypass to get in while that is happening, and do not expose Wishlistarr directly to the internet.

## Antiforgery

Writes (`POST`, `PUT`, `PATCH`, `DELETE`) made with a session cookie or through the trusted-network bypass must send an antiforgery token. Fetch it after signing in with `GET /api/v1/auth/antiforgery`, which returns `{ "token", "headerName" }`, and send the token in the `X-CSRF-TOKEN` header. A token fetched before sign-in is not valid afterwards. Missing or invalid tokens get `400`.

## API key

The API key is generated on first start, printed to the console, and stored encrypted in the settings table. Send it in the `X-Api-Key` header. It authenticates as a Parent and does not need an antiforgery token, since browsers never send it on their own.

## Trusted-network bypass

The bypass treats every request from configured networks as a signed-in parent with no password. It is off by default.

```json
{
  "Auth": {
    "TrustedNetworkBypass": true,
    "TrustedNetworks": ["192.168.1.0/24"],
    "TrustedHosts": ["wishlistarr.lan", "192.168.1.10"],
    "KnownProxies": ["192.168.1.2"]
  }
}
```

`TrustedNetworks` are CIDR ranges written with full IP addresses, such as `192.168.1.0/24`. `KnownProxies` are the exact addresses of reverse proxies in front of Wishlistarr, also written in full; shorthand such as `10.1` and IPv4 parts with leading zeros such as `010` are rejected. `X-Forwarded-For` is only read from those addresses; when the list is empty the header is ignored entirely.

`TrustedHosts` lists the host names (without port) that browsers use to reach Wishlistarr. It is required while the bypass is on, and a request is only bypassed when its `Host` header matches one of them. Wildcards and IPv6 literal hosts are not accepted. A reverse proxy in front of Wishlistarr must pass the original `Host` header through unchanged and only serve the trusted host names; a proxy that rewrites `Host` to its upstream name makes every request match and defeats this check. Without this check a web page on another site could use DNS rebinding to point its own host name at Wishlistarr, and the parent's browser on a trusted network would then read the antiforgery token and make writes as a parent.

Invalid entries, or the bypass turned on without `TrustedHosts`, stop the app from starting.

Risks to weigh before turning it on:

- Anyone on a trusted network gets full parent access: kids on the home Wi-Fi, guests, and any compromised device. Pick ranges that only parent devices use, such as a separate VLAN, rather than the whole home network.
- If a reverse proxy sits in front of Wishlistarr and is not listed in `KnownProxies`, every request appears to come from the proxy's address. If that address is inside a trusted network, the whole internet gets parent access.
- Only the last `X-Forwarded-For` entry, the one added by the listed proxy, is used. Chains of several proxies are not supported.
- Any request from a trusted address that carries no API key or session cookie is treated as a parent, so a kid's device on a trusted range has parent access.
