# Preset-password access control

> The tool was originally single-user with no authentication. This adds one gate: entering the
> system requires a password, preset through an environment variable, with no username. It is
> **access control**, not part of the encryption scheme — it does not change what the Data
> Protection key ring is for, and it does not turn the user's password into a master key for any
> data.
>
> Supplements [product-requirements.md](product-requirements.md). There is one mandatory
> interaction with [keyring-loss-recovery-design.md](keyring-loss-recovery-design.md), in §5.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | Session mechanism | **Cookie signed by Data Protection.** The key ring already exists, so reuse it; `HttpOnly` puts it out of reach of XSS; expiry and sliding renewal are built in; logging out just deletes the cookie. Not a localStorage token (readable by XSS), not HTTP Basic (no custom login page, and logging out is awkward) |
| 2 | When no password is set | **Allow everything**, with a Warning logged at startup. Authentication is optional hardening, so existing deployments behave identically after upgrading. No UI banner |
| 3 | Password storage | Plaintext in the `Auth__Password` environment variable. No pre-hashing — that would require the user to run a tool to compute a hash first, a disproportionate burden for a self-hosted single-user tool |
| 4 | Protection scope | `/api/*` only; static assets are unprotected |
| 5 | Exemptions | Exactly six: `GET /api/health`, `GET /api/health/ready`, `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/status`, and the SPA fallback. Marked **per endpoint**, never on the group — otherwise an endpoint added to `/api/auth` later would silently inherit anonymous access. The list is pinned by tests |
| 6 | Unauthenticated response | **401**, no redirect. The frontend is an SPA using `fetch`, and a redirect would just hand `fetch` a page of HTML |
| 7 | Session lifetime | Sliding expiry, 30 days |
| 8 | Relationship to keyring recovery | The login gate sits **outside** the keyring gate. Password comparison never touches the key ring, so logging in still works while the ring is `Lost` |

## 2. Configuration

`Auth__Password` (configuration key `Auth:Password`, following the project's existing `Section__Key` convention).

- **Unset or empty** → authentication off, every endpoint open, with a Warning at startup: `Authentication is disabled: Auth__Password is not set.`
- **Set** → authentication on

The image sets no default for it — a default password is more dangerous than no password.

## 3. The session

ASP.NET Core cookie authentication, signed by the existing Data Protection key ring.

| Property | Value | Reason |
|---|---|---|
| `HttpOnly` | `true` | Unreadable by XSS |
| `SameSite` | `Lax` | CSRF protection without breaking normal navigation |
| `SecurePolicy` | **`SameAsRequest`** | See below |
| Expiry | Sliding, 30 days | No re-login during normal use; only long disuse expires it |

**`SecurePolicy` must be `SameAsRequest`, never hard-coded to `Always`.** The image listens on HTTP by default (`ASPNETCORE_URLS=http://+:8080`). Forcing `Secure` means the browser will not send the cookie back over HTTP at all, which presents as "login succeeds and immediately asks for login again" — a failure that is very hard to trace back from the symptom. `SameAsRequest` adds `Secure` under HTTPS and omits it under HTTP.

## 4. Endpoints and middleware

### 4.1 Endpoints

| Endpoint | Request | Response |
|---|---|---|
| `POST /api/auth/login` | `{ "password": "..." }` | Correct → **204** plus `Set-Cookie`; wrong → **401** |
| `POST /api/auth/logout` | — | **204**, cookie cleared |
| `GET /api/auth/status` | — | `{ "required": bool, "authenticated": bool }` |

`status` is the frontend's only basis for deciding what to render, so it must be reachable while unauthenticated.

### 4.2 Where the middleware sits

```
UseCors → UseDefaultFiles → UseStaticFiles → [auth] → UseSecretUnavailableMapping → Map*Endpoints → MapFallbackToFile
```

Authentication goes after `UseStaticFiles` (static assets are unprotected) and before `UseSecretUnavailableMapping` (decide whether they may come in, then handle business exceptions on the inside), and applies only to the `/api/` prefix.

Static assets (HTML/JS/CSS) are **not** protected: they contain no sensitive data — everything comes through the API — and protecting them would lock the login page itself outside the gate.

Health probes must be exempt. Otherwise `docker healthcheck` and any orchestrator probe get a 401, the container is judged unhealthy and restarts in a loop — an availability failure caused directly by "improving security".

But the exemption covers **reachability**, not **information**: the 200/503 of `/api/health/ready` is unchanged (that is what probes consume), while the `database` and `keyring` booleans are only returned when authenticated, or when authentication is off. Otherwise any anonymous prober could read "this instance is in keyring recovery mode".

### 4.3 Security details

- Password comparison uses `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes. Timing side-channel protection, at zero cost.
- A failed login sleeps about one second, and that delay is **serialised process-wide** (`SemaphoreSlim(1,1)`), which makes online brute force uneconomical. Sleeping one second per request independently does not work: with N requests in flight the amortised cost per attempt approaches zero, whereas serialised, N failures take N seconds of real time. Only the failure path serialises — a successful login never queues. **No account lockout**: on a single-user tool, lockout means locking yourself out.
- Every failed login logs a Warning recording the source IP and **never the submitted password**, so brute force leaves a trace.
- The password is never written to a log and never appears in an error response.

### 4.4 CORS (local development only)

The existing policy lacked `AllowCredentials()`. Cross-origin requests do not carry cookies by default, so running the Vite dev server (`localhost:5173` against a backend on `localhost:8080`) could not log in. Production is a same-origin single image and was unaffected.

## 5. Interaction with keyring recovery (mandatory)

The login gate must sit **outside** the keyring gate, and the login path must not depend on Data Protection to decrypt anything.

- Password comparison reads the plaintext environment variable and never touches the key ring → **login still works** while the ring is `Lost`.
- The cookie is signed by the key ring → losing `/keys` invalidates existing sessions and requires one fresh login.

The correct sequence is: key ring lost → log in again (the password comes from the environment and is unaffected) → enter the system → see the recovery banner → reset credentials one by one.

Putting the login gate *after* `KeyringGuard`, or making login depend on the key ring, creates a deadlock: **recovery requires logging in, and logging in requires recovery.**

## 6. Frontend

On mount, `App.tsx` requests `GET /api/auth/status` and picks one of three renderings:

| State | Renders |
|---|---|
| `required: false` | The main UI, exactly as before |
| `required: true, authenticated: false` | **Only** the login page |
| `required: true, authenticated: true` | The main UI plus `Log out` in the nav bar |

While unauthenticated, main-UI components are **not mounted** rather than covered by an overlay — components under an overlay still issue requests, producing a burst of 401 noise.

The API client treats any 401 as "no longer authenticated" and flips the app back to the login page, which covers the case of a cookie expiring while the UI is still in use.

`fetch` defaults `credentials` to `same-origin`: cookies are sent automatically under same-origin deployment, but under a cross-origin deployment the browser will not send them at all, which would render the backend's `AllowCredentials()` pointless. The client therefore sets `credentials: 'include'` throughout (a superset of `same-origin`, so same-origin behaviour is unchanged).

The login page holds one password field and one button.

## 7. Pinned behaviour

With no password set, everything is open and `status` reports `required: false`. With one set, unauthenticated requests get 401, the correct password returns 204 plus a cookie that subsequent requests are accepted with, a wrong password returns 401 and issues no cookie, and logging out invalidates the cookie. The login and status endpoints are themselves never intercepted — otherwise there would be no way in.

**Health probes still return 200 when a password is set.** This is the one most likely to be broken by a later refactor, and breaking it restarts the container in a loop.

## 8. Deployment note

Production should sit behind an HTTPS reverse proxy. Over plain HTTP both the password and the cookie travel in the clear; this gate stops people who do not know the password, not people who can sniff the traffic.

## 9. Deliberately not done

- No usernames, multiple users or roles
- No password-change UI (change the variable and restart)
- No account lockout (a single-user tool would lock itself out)
- The user's password is never used as a master key for encrypting data
- Static assets stay unprotected
