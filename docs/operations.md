# Operations, security boundaries and recovery

Everything about running the container: what protects it, what constrains it, and how it recovers
when the machine it runs on loses something.

## Access control

The tool is **single-user** — no usernames, no account model, no permission model. What it has is an
**optional** password gate in front of the whole UI.

`Auth__Password` (configuration key `Auth:Password`):

- **Unset or empty** → authentication off, every endpoint open, with a Warning logged at startup:
  `Authentication is disabled: Auth__Password is not set.`
- **Set** → a login is required.

The image sets no default. A default password is more dangerous than no password.

| Decision | Conclusion |
|---|---|
| Session mechanism | a cookie signed by the Data Protection key ring |
| Password storage | plaintext in the environment variable, no pre-hashing |
| Protection scope | `/api/*` only; static assets are unprotected |
| Unauthenticated response | **401**, never a redirect |
| Session lifetime | sliding expiry, 30 days |

> **Rationale — a cookie rather than a localStorage token or HTTP Basic.** The key ring already
> exists, so signing costs nothing new. `HttpOnly` puts the cookie out of reach of XSS, expiry and
> sliding renewal are built in, and logging out is just deleting it. A localStorage token is readable
> by XSS; HTTP Basic gives no custom login page and makes logging out awkward.

> **Rationale — 401 rather than a redirect.** The frontend is an SPA using `fetch`, and a redirect
> would just hand `fetch` a page of HTML.

> **Rationale — no pre-hashing.** It would require the user to run a tool to compute a hash before
> they could set a password: a disproportionate burden for a self-hosted single-user tool.

### The cookie's `SecurePolicy` must be `SameAsRequest`

Never hard-coded to `Always`.

> **Rationale.** The image listens on HTTP by default. Forcing `Secure` means the browser will not
> send the cookie back over HTTP at all, which presents as "login succeeds and immediately asks for
> login again" — a failure that is very hard to trace back from the symptom. `SameAsRequest` adds
> `Secure` under HTTPS and omits it under HTTP.

### Middleware order and exemptions

```
UseCors → UseDefaultFiles → UseStaticFiles → [auth] → UseSecretUnavailableMapping → endpoints → SPA fallback
```

Authentication goes after static files (they are unprotected) and before the secret-unavailable
mapping — decide whether they may come in, then handle business exceptions on the inside.

Exactly six endpoints are exempt: the two health probes, login, logout, auth status, and the SPA
fallback. They are marked **per endpoint, never on the group**.

> **Rationale.** An endpoint added to `/api/auth` later would otherwise silently inherit anonymous
> access. The list is pinned by tests.

**Health probes must be exempt**, or `docker healthcheck` and any orchestrator probe get a 401, the
container is judged unhealthy, and it restarts in a loop — an availability failure caused directly by
"improving security". But the exemption covers **reachability, not information**: the 200/503 of the
readiness probe is unchanged, while its `database` and `keyring` booleans are returned only when
authenticated. Otherwise any anonymous prober could read "this instance is in keyring recovery mode".

### Brute-force resistance

- Password comparison uses `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes.
- A failed login sleeps about one second, and that delay is **serialised process-wide**.

> **Rationale — why serialised.** Sleeping one second per request independently does not work: with
> N requests in flight the amortised cost per attempt approaches zero. Serialised, N failures take N
> seconds of real time. Only the failure path serialises; a successful login never queues.

- Every failed login logs a Warning with the source IP and **never** the submitted password.
- **No account lockout.** On a single-user tool, lockout means locking yourself out.

### The frontend

`GET /api/auth/status` decides one of three renderings: the main UI (no password set), the login page
alone, or the main UI plus a `Log out` control. While unauthenticated, main-UI components are **not
mounted** rather than covered by an overlay — components under an overlay still issue requests,
producing a burst of 401 noise. Any 401 flips the app back to the login page, which covers a cookie
expiring mid-use.

The API client sets `credentials: 'include'` throughout — a superset of the `same-origin` default, so
same-origin behaviour is unchanged, while a cross-origin dev setup can log in at all.

> **Deployment note.** Production should sit behind an HTTPS reverse proxy. Over plain HTTP both the
> password and the cookie travel in the clear; this gate stops people who do not know the password,
> not people who can sniff the traffic.

## The local path boundary

`Backup__Root` constrains every local path operation — backup, restore, repair, browse.

- **Unset or empty** → no boundary, behaviour identical to having none.
- **Set** → the root's own real path is resolved once at startup and cached.

Resolving the root itself first is mandatory: if `/nas` is a symlink to `/mnt/disk1`, comparing the
literal string against resolved real paths would reject every legitimate path.

The root is **a security filter only**. It does not rewrite paths, truncate them, or serve as the base
for relative paths — storage, display and logs all carry the full original path.

### Judging the boundary, segment by segment

1. Normalise with `Path.GetFullPath`, removing `..`, `.` and repeated separators.
2. Expand symlinks **one segment at a time** to reach the real path.
3. Compare against the resolved root **on segment boundaries**, so `/nasty` does not pass by
   prefix-matching `/nas`. Equality with the root counts as inside.
4. Cap the depth for symlink cycles; exceeding it is judged out of bounds, not an exception and not
   an infinite loop.
5. For a path that does not exist, judge its **nearest existing ancestor** — a restore target may be
   a directory not yet created, and "does not exist yet" is not grounds for rejection.

> **Rationale — why not `Directory.ResolveLinkTarget`.** .NET has no `realpath`, and that method
> resolves **only the last segment**: if `/nas/link` points at `/etc`, querying `/nas/link/passwd`
> returns null, because `passwd` itself is not a link. Relying on it misses every case where an
> **intermediate** segment is a symlink — precisely the shape most easily exploited. And "use
> symlinks to gather scattered directories into one place" is exactly the usage this feature is aimed
> at, so symlinks cannot simply be refused.

### Where it is validated

Every operation, not just on save: creating a configuration, starting a backup, check, repair or
cleanup, starting a restore (including when the target falls back to the local root), and the browse
API for both the requested path and every child returned.

> **Rationale.** The boundary means "no configuration may cross it regardless of where it came from",
> and configurations can come from an older version, a hand-edited database, or an arbitrary
> container imported through `/import`.

**Existing out-of-bounds configurations are kept, not deleted.** They still appear in the list, while
backup, restore, check and repair all return 409 with `path_outside_root`, naming both the root and
the rejected path. Startup is not blocked.

### Browsing

`GET /api/system/browse?path=...` returns direct children only, lazily. Both directories and files
come back; only directories are selectable, because both the local root and a restore target are
directories by definition — while being able to see files is how you confirm you picked the right
place.

Out-of-bounds children are **returned with an `outsideRoot` flag** rather than filtered out.

> **Rationale.** If `/nas/link → /etc` simply did not appear, the user would be confused about a
> directory entry they can plainly see elsewhere. Returning it with a flag explains why it cannot be
> used.

A child that cannot be read is skipped while the rest are still returned, so one failure does not
fail the request. Results are paged, and truncation is **stated explicitly**, never silently short.

Restore's own path-traversal defence is independent of this boundary and applies even when it is
unset — see [check-restore-repair.md](check-restore-repair.md).

## Key ring loss and recovery

The Data Protection key ring under `/keys` encrypts three fields: the account key, the proxy
password, and the backup password. Losing it makes all three undecryptable.

**The design principle: store ciphertext, decrypt at the chokepoints.**

> **Rationale — the root cause it replaced.** A ValueConverter decrypted unconditionally at **entity
> materialisation**, regardless of whether the caller wanted the field. Listing accounts to read
> nothing but their names still called decrypt on every row's key. Once the key ring was gone that
> threw, no path caught it, and the account list and backup list both returned 500 wholesale — the
> user could not reach the UI at all, let alone repair anything.
>
> The key observation is that the places which genuinely *read* these fields are very few; everything
> else is transport, and transporting ciphertext is exactly as good as transporting plaintext.

| Consumer | Chokepoint |
|---|---|
| account key, proxy password | the blob client factory — the sole entry to every cloud operation |
| backup password | the request mapper's password accessor, and one shared helper in the config endpoints |

The three properties carry a `Protected` suffix while `HasColumnName` pins the original column names,
so **existing data needs no migration** — what the ValueConverter wrote was already ciphertext.

> **Rationale for the rename.** The compile errors from renaming *are* the list of call sites to
> audit.

Two things needed adjusting:

1. "Is this an encrypted backup?" tested with `!string.IsNullOrEmpty(Password)` still works: non-empty
   ciphertext ⟺ non-empty plaintext.
2. Comparing `update.Password != existing.Password` **had to change**. Data Protection uses a random
   IV, so the same plaintext encrypts differently each time and ciphertexts cannot be compared. That
   line meant "the password cannot be changed after creation", which is now enforced directly: the
   ordinary `PUT` rejects any non-empty password, and resets go through a dedicated endpoint.

### The canary

A single-row table holds the ciphertext of a known constant, read and written **with no converter**,
using explicit protect/unprotect — otherwise the canary itself would be swallowed by the degradation
logic and lose all diagnostic value. A singleton holds `Healthy | Lost`, judged once at startup.

| Canary row | Probe source | Conclusion |
|---|---|---|
| present, decrypts | itself | `Healthy` |
| present, fails, undecryptable ciphertext remains | full scan | `Lost` |
| present, fails, no undecryptable ciphertext remains | full scan | rebuild the canary, `Healthy` |
| absent | lowest-id account's key | decrypts → write canary, `Healthy`; fails → `Lost` |
| absent, no accounts | lowest-id encrypted backup config | as above |
| absent, neither exists | — | brand-new database → write canary, `Healthy` |

> **Rationale — the "absent" row is the mandatory branch for upgrading an older database.** Blindly
> writing a new canary and declaring `Healthy` would miss "the key ring was already lost at upgrade
> time" — and miss it forever, since the new canary is written by the new key ring and will always
> decrypt. The probe deliberately takes the **lowest-id** row: `FirstOrDefault` without `OrderBy` has
> undefined ordering in EF, which would make the judgement irreproducible.

> **Rationale — the third row is a startup backstop that cannot be omitted.** Consider "key ring lost
> → the user gives up and deletes every account and encrypted configuration". If the delete endpoints
> did not get to finish, there would be no ciphertext left in the database while a stale canary pins
> the status to `Lost`: readiness permanently 503, the scheduler skipping everything, every action
> 409, and the banner reading "**0** credentials need to be re-entered" — with no way out until a
> restart passes through here.

### What `Lost` mode allows

- The scheduler skips every task, logging **one** summary Warning per tick, not one per task.
- Manual backup, restore, check and cleanup return **409** with code `keyring_lost`.
- The account and backup configuration lists **still return**, carrying `secretsUnavailable: true`.
- Readiness returns 503 `degraded`.
- The only permitted write is a credential reset.

**The pending count and per-row flags are computed from each record's actual decryptability, never
from the global status.**

> **Rationale — this is what stops the recovery flow deadlocking.** "`Lost` means nothing decrypts"
> holds only at the instant of loss. Recovery necessarily passes through a state where every account
> has been reset while backup passwords are still old ciphertext. The global status must still be
> `Lost` there, but the account pending count must already have reached zero — because the UI keeps
> backup-password resets disabled until accounts reach zero. Counting from the global status would
> make that count permanently non-zero: the button would never enable, the password could never be
> reset, and the status could never flip.

When `Healthy` this short-circuits to zero, so the list endpoints still trigger no decryption at all.

### The reset flow

```
POST /api/accounts/{id}/reset-secrets       { accountKey, proxyPassword? }
POST /api/backup-configs/{id}/reset-password { password }
```

**Verify before persisting.** Accounts use the existing connection test. Backup passwords are
verified by fetching the encrypted info file from the cloud and decrypting it.

> **Rationale.** The info file of an encrypted backup is itself a 7z encrypted with that password. It
> is the metadata root of the whole backup, the smallest encrypted object in the container, and
> touching it neither reads data packs nor triggers Archive retrieval fees.

Verification must use the plain read path, **not** the tracked store's seed-from-cloud method: it is
an operation that may fail and be retried repeatedly, and it must have no side effects.

**Recovery order is accounts first, then backup configurations**, enforced by the UI — verifying a
backup password requires the cloud, and reaching the cloud requires the account key.

**The completion check probes every record holding ciphertext** and only rebuilds the canary once all
succeed. Flipping on the first successful reset would be wrong; the rest still do not decrypt.

### The login gate sits outside the keyring gate

Password comparison reads the plaintext environment variable and never touches the key ring, so
**login still works while the ring is `Lost`**. The cookie is signed by the ring, so losing `/keys`
invalidates existing sessions and requires one fresh login.

> **Rationale.** Putting the login gate *after* the keyring guard, or making login depend on the ring,
> creates a deadlock: **recovery requires logging in, and logging in requires recovery.** The correct
> sequence is: ring lost → log in again → enter the system → see the recovery banner → reset
> credentials one by one.

### No escape hatch for a forgotten backup password

There is no "abandon history and use a new password", and no re-encryption migration of historical
packs. A forgotten password means deleting that configuration and starting over.

## 7-Zip CPU priority

Compression and extraction are the only things this program does that saturate a CPU, and it runs on
a NAS that is also running a media library, a photo indexer and somebody else's containers. A backup
is background work: nobody notices it being slower, everybody notices the machine stalling.

```csharp
public enum SevenZipCpuPriority { Lowest = 0, BelowNormal = 1, Normal = 2 }
```

| Value | `ProcessPriorityClass` | Linux nice |
|---|---|---|
| `Lowest` *(default)* | `Idle` | 19 |
| `BelowNormal` | `BelowNormal` | 10 |
| `Normal` | `Normal` | 0 |

There is no "above normal": raising priority on Linux requires privileges, and letting compression
outrank the web UI has no upside for a background backup program.

> **Rationale — why priority and not just thread count.** `-mmt=N` was already adjustable through an
> environment variable, but that requires a container restart, and capping threads reduces
> **parallelism**, not **scheduling weight under contention**. One saturated thread can still make
> the UI stutter.

> **Rationale — `Lowest` must be 0 in the enum.** The EF migration fills existing rows with 0, so an
> upgraded database lands on "lowest" naturally, matching the default. The counter-example is
> `StagedLimitBytes` and `ProcessingMaxAttempts`, whose valid defaults are not 0 — which is why the
> settings service still carries a "if it reads 0, substitute the default" patch. Defining `Lowest`
> as 0 avoids incurring that debt again.

The setting is passed as a **delegate rather than a value**, so saving a change applies to the next 7z
process without restarting the container. It reaches every 7z invocation: backup compression
including the streaming path, restore extraction, deep check, repair, dead-weight compaction, and
index encoding/decoding.

> **Two traps that must stay in the comments.**
>
> **Failing to set priority is swallowed unconditionally.** The process may already have exited in
> those few microseconds, and the platform may refuse. Not being able to lower priority is not a
> compression failure and must never take a backup down with it.
>
> **On Linux, nice is a per-thread attribute.** `setpriority(PRIO_PROCESS, pid)` lands on the main
> thread only, and 7z's LZMA workers inherit the nice value of **the thread that created them**, at
> creation time. Setting it the instant `Process.Start` returns means 7z is still dynamically linking
> and parsing arguments with no workers created yet, so in practice they all inherit it. The worst
> case — losing that race — is that some threads stay at the old priority: no effect on correctness,
> only on effectiveness.

## 7-Zip binary

The image fetches the **official `7zz` binary** for the target architecture at build time, not the
distro package.

> **Rationale.** p7zip and 7-Zip 23.01 write a **zero attribute** for `-si` stdin input, which makes
> single-file blobs unrestorable. This was measured, not assumed.

Commands: `7zz a -p{pwd} -mhe=on -v{size} out.7z ...` for AES-256 with header encryption and volume
splitting; `7zz x out.7z.001` to extract.

## Settings storage

SQLite holds accounts, groups, schedules, defaults and logs.

> **Rationale.** Logs need filtering by level, time and source, and schedules need querying — a
> database fits better than files, and the skeleton already had EF Core plus SQLite.

The **info file is a separate thing**, stored in the Azure container, not in the local database.

Secrets are stored reversibly encrypted with the Data Protection key ring; everything else is stored
in the clear.

## Environment variables

| Variable | Effect |
|---|---|
| `Auth__Password` | the UI password gate; unset means open |
| `Backup__Root` | the local path boundary; unset means unrestricted |
| `Backup__SevenZipMethodArgs` | extra 7z arguments, e.g. `-mmt=N` |
| `Scheduler__Enabled` | whether the background scheduler runs |

Azure credentials are **not** configured through environment variables — each storage account is
added in the UI and its key is encrypted at rest.

`Backup__Root` constrains paths **inside the container**, so it works together with volume mounts:
mount every host directory you want to back up beneath that root.

## Shutdown timing

```
docker stop_grace_period 45s  >  HostOptions.ShutdownTimeout 30s  >  waiting for runs to flush 20s
```

The three form one chain, and changing any one means revisiting the other two. The reasoning is in
[run-lifecycle.md](run-lifecycle.md).

## See also

- [run-lifecycle.md](run-lifecycle.md) — graceful shutdown and automatic resume
- [check-restore-repair.md](check-restore-repair.md) — restore's own path-traversal defence
- [configuration.md](configuration.md) — what is configured per backup rather than per deployment
- [web-ui.md](web-ui.md) — the login page and the recovery banner
