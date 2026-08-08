# Detecting and recovering from key ring loss

> When the Data Protection key ring (`/keys`) is lost, every encrypted field in the database
> becomes undecryptable. The original implementation threw during EF entity materialisation, so
> the account list and the backup configuration list returned 500 wholesale — the user could not
> even reach the UI, let alone repair anything. The answer: **store ciphertext, decrypt on
> demand**, with a canary for detection and a guided re-entry flow.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | When ciphertext is decrypted | **Drop the ValueConverter; store ciphertext as-is.** Decryption happens only at the chokepoints that genuinely need the content. Lists and the main UI trigger none, so the UI stays usable with the key ring gone |
| 2 | Property naming | Sensitive properties gain a `Protected` suffix, with `HasColumnName` keeping the database column names unchanged. The compile errors from renaming *are* the list of call sites to audit |
| 3 | Health detection | A single-row `KeyringCanary` table holds the ciphertext of a known constant, **bypassing all converters**, with the service layer calling `Protect`/`Unprotect` explicitly. Judged once at startup and cached in a singleton |
| 4 | Judging an upgraded database | With no canary row, **deterministically take the lowest-`Id` Account** and try to decrypt its key (falling back to the lowest-`Id` BackupConfig with a non-empty password if there are no accounts). "Any one row" will not do — `FirstOrDefault` without `OrderBy` has undefined ordering in EF, which makes the judgement irreproducible |
| 5 | Verifying a reset password | **Verify before persisting.** Accounts use the existing connection test; backup passwords are verified by fetching the encrypted info file from the cloud and decrypting it — it is the metadata root of the backup, the smallest encrypted object in the container, and touching it neither reads data packs nor triggers Archive retrieval fees |
| 6 | Escape hatch for a forgotten backup password | **There is none.** No "abandon history and use a new password", and no re-encryption migration of historical packs. A forgotten password means deleting that backup configuration and starting over |
| 7 | Recovery order | **Accounts first, then backup configurations.** Verifying a backup password requires the cloud, and reaching the cloud requires the account key — a physical constraint the UI enforces |
| 8 | Backup passwords are immutable | The ordinary PUT path rejects any non-empty `Password`; resets go through a dedicated endpoint |
| 9 | `ConnectionStrings__AzureStorage` | **Deleted**, along with the global `BlobServiceClient` singleton and the storage service built on it |
| 10 | `/api/health/ready` | Kept, but changed to a **purely local** readiness check (SQLite opens, canary decrypts), with zero cloud reads |

## 2. The root cause

Three sensitive fields were encrypted and decrypted automatically at the persistence boundary by a ValueConverter: the account key, the proxy password, and the backup password.

**The root cause**: a ValueConverter decrypts unconditionally at **entity materialisation**, regardless of whether the caller wants that field. Listing accounts to read nothing but their names still called `Decrypt` on every row's key ciphertext. Once the key ring was gone that threw, and no code path caught it or degraded, so the list query failed entirely.

**The key observation**: the places that genuinely *read* these three fields are very few; everything else is transport — and transporting ciphertext is exactly as good as transporting plaintext.

| Consumer | Purpose |
|---|---|
| Account key | Building `StorageSharedKeyCredential` in the blob client factory |
| Proxy password | Building `NetworkCredential` in the same factory |
| Backup password | Passed to 7z as `-p` by the compressor and the archive codec |

Everything else — service layers, endpoint handlers, request mappers, runners, the dispatcher, the orchestrators — moves the value without interpreting it, and needs no plaintext.

## 3. The design

### 3.1 Ciphertext in the database, decryption at the chokepoints

The ValueConverters are removed and the three fields store ciphertext in EF, with the service layer encrypting and decrypting explicitly. Properties gain the `Protected` suffix while `HasColumnName` pins the original column names.

**Where decryption lands** (chokepoints, not scattered checks):

- **Account key and proxy password** — inside the blob client factory. `CreateServiceClient` is the sole entry to every cloud operation, so any path that reaches Azure must pass through it, and decrypting there covers all of them without repeating it per action.
- **Backup password** — two concentration points: the request mapper's password accessor, and a helper replacing the six copies of `var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;` scattered through the backup config endpoints. Collapsing those into one helper is both the decryption point and the removal of a duplication.

Downstream, plaintext still flows exactly as before, so no transport code changes. A failed decryption throws `SecretUnavailableException`, which pins the error to the action that triggered it.

That exception and the 409 gate in §3.3 are two different layers. When the canary says `Lost`, action endpoints fail fast at the entry with 409 and normally never reach a decryption site; the exception is defence in depth for cases the canary has not covered — a new code path bypassing the gate, or the key ring being replaced while the process runs — so that failure is unambiguous rather than producing a pack encrypted with the wrong password.

**The property this buys**: the main UI and both lists query only non-sensitive fields. When `Healthy`, no decryption is triggered at all. While `Lost`, computing the per-row `secretsUnavailable` flags (§3.3) does attempt one `TryDecrypt` per record — but that call does not throw, so the lists remain fully usable with the key ring gone. Only concrete actions (listing containers, backing up, restoring, checking) need credentials and can throw.

**Problems this dissolves on its own**: the "submitting an empty value keeps the existing one" logic in the account and backup-config endpoints becomes a straight copy of the existing ciphertext — correct, and requiring no decryption.

**Two things that needed adjusting:**

1. Checking "is this an encrypted backup?" with `!string.IsNullOrEmpty(Password)` still works: non-empty ciphertext ⟺ non-empty plaintext. **No change needed.**
2. Comparing `update.Password != existing.Password` **had to change.** Data Protection uses a random IV per encryption, so the same plaintext encrypts to different ciphertext each time and ciphertexts cannot be compared. That line's intent was "the password cannot be changed after creation", which per decision 8 becomes: the ordinary PUT path rejects any non-empty `Password` with *Password cannot be changed after creation; leave it empty.* The only behavioural change is that resubmitting the identical password now fails instead of passing — and the frontend has always used an empty field to mean "unchanged".

### 3.2 The canary and status detection

A single-row `KeyringCanary` table holds the ciphertext of the constant `canary.v1`, read and written **with no converter**, using explicit `Protect`/`Unprotect` — otherwise the canary itself would be swallowed by the degradation logic and lose all diagnostic value.

A singleton holds `KeyringStatus = Healthy | Lost`, judged once at process start, cached, and flipped explicitly when the reset flow completes.

**Startup judgement:**

| Canary row | Probe source | Conclusion |
|---|---|---|
| Present and decrypts | The canary itself | `Healthy` |
| Present, fails, and undecryptable ciphertext remains | Full scan of all three families | `Lost` |
| Present, fails, but no undecryptable ciphertext remains | Full scan | Rebuild the canary, `Healthy` |
| Absent | The lowest-`Id` Account's key ciphertext | Decrypts → write the canary, `Healthy`; fails → `Lost` |
| Absent, and no accounts | The lowest-`Id` BackupConfig with a password | As above |
| Absent, and neither exists | — | Brand-new database → write the canary, `Healthy` |

The fourth row is the mandatory branch for upgrading an older database. Without a canary row, blindly writing a new one and declaring `Healthy` would miss "the key ring was already lost at upgrade time" — and miss it forever, since the new canary is written by the new key ring and will always decrypt.

The fifth row is a cheap backstop, costing one extra query only when there is not a single account, and it closes the narrow window where all accounts were deleted but backup configurations remain.

The third row is the startup backstop for `Lost`, and it cannot be omitted. Outside the canary itself, the thing that rewrites it is the completion check in §3.4 — reached from the two reset endpoints and from the account and backup-config delete endpoints (deleting the last pending record triggers it too), all of which require at least one record to exist at that moment. So "key ring lost → the user gives up and deletes every account and encrypted backup configuration" could, if the delete endpoints did not get to finish (direct database edits, or an older build), leave no ciphertext in the database at all while a stale canary pins the status to `Lost`: `/api/health/ready` permanently 503, the scheduler skipping everything, every action 409, and the banner reading "**0** credentials need to be re-entered" — no way out until a restart passes through here. Judging this row by the same full scan §3.4 uses guarantees that "`Lost` with zero pending resets" cannot become permanent: the delete endpoints clear it during the run, and this row catches it at the next start.

### 3.3 What `Lost` mode allows

- The scheduler skips every task, logging **one** summary Warning per tick (not one per task, which would flood the log)
- Manually triggering backup / restore / check / cleanup returns **409** with error code `keyring_lost`
- The account and backup configuration lists **still return**, carrying `secretsUnavailable: true`
- `/api/health/ready` returns 503 `degraded`
- The only permitted write is a credential reset

The pending count and the per-row `secretsUnavailable` flags **must be computed from each record's actual decryptability**, never from the global status.

"`Lost` means nothing decrypts" holds only at the instant the key ring is lost. Recovery necessarily passes through an intermediate state: every account has been reset successfully while backup passwords are still old ciphertext. The global status must still be `Lost` there (§3.4 requires all three families to decrypt), but the account pending count must already have reached zero. Counting from the global status instead would make the ordering dependency in §3.5 — backup password `Re-enter` stays disabled until accounts reach zero — read a permanently non-zero account count. The button would never enable, the password could never be reset, the status could never flip: **the recovery flow deadlocks in the UI.**

So while `Lost`, the ciphertext columns are probed row by row: accounts check the key and the proxy password (one reset covers both), backup configurations check the password (unencrypted ones have nothing to lose and are neither counted nor flagged). When `Healthy` it short-circuits to zero, so the list endpoints still trigger no decryption at all and §3.1's core property is untouched. Record counts are small, on the same order as §3.4's completion scan, and the cost is negligible.

### 3.4 The reset flow

Two dedicated endpoints, not a reuse of PUT (PUT should be restricted wholesale in recovery mode):

- `POST /api/accounts/{id}/reset-secrets` — body carries `accountKey` and optionally `proxyPassword`; the existing connection test runs first and only a pass persists.
- `POST /api/backup-configs/{id}/reset-password` — body carries `password`; the encrypted info file is fetched from the cloud and decrypted, and only a pass persists.

**Why the info file verifies the password**: the info file of an encrypted backup is itself a 7z encrypted with that password. It is the metadata root of the whole backup, holding only the version list and similar, and is the smallest encrypted object in the container. Decrypting it proves the password.

**An implementation constraint**: verification must use the plain read path, **not** the tracked store's seed-from-cloud method — the latter backfills local authoritative state. Verification is an operation that may fail and may be retried repeatedly, and it must have no side effects.

Unencrypted backup configurations have no key to lose and never enter the pending list.

**The completion check**: probe every record holding ciphertext, and only once all succeed rebuild the canary and flip the status back to `Healthy`. Flipping on the first successful reset would be wrong — the rest still do not decrypt.

### 3.5 The guided UI

- In recovery mode, a persistent banner: `Data protection keys were lost — N credentials need to be re-entered`, expanding to the pending list
- The list is grouped **Accounts → Backup Configs**, with the second group disabled until the first is complete, expressing decision 7's ordering dependency
- On each page, affected rows show a badge and a `Re-enter` button opening a dialog with just the one field, with verifying / verification-failed feedback
- Backup, restore and check buttons are disabled in recovery mode, with a tooltip explaining why

### 3.6 Dead code removed

`ConnectionStrings__AzureStorage` fed a global `BlobServiceClient` singleton, which fed a storage service used by nothing but `/api/health/ready`. The frontend never called that endpoint and no test referenced the service. Real backups go through `BlobClientFactory.CreateServiceClient(account)` and never touched this chain. The probe also issued a `GetProperties` to the cloud on every call, which conflicts with the "zero cloud reads at run time" principle.

`/api/health/ready` now checks that SQLite opens and the canary decrypts — both local.

## 4. Data and migration

**Existing data needs no migration.** What the ValueConverter wrote to disk was already `_protector.Protect(plaintext)`, so storing ciphertext as-is produces an identical on-disk format. Renaming properties while pinning column names produces no column changes either.

The only migration creates the `KeyringCanary` table, which the startup `Migrate()` call applies automatically.

## 5. Pinned behaviour

The canary's four branches (brand-new database; upgraded database that decrypts; upgraded database that does not; canary present but undecryptable) each reach the documented conclusion. Ciphertext round-trips: what is written is ciphertext, what is read back is ciphertext, and the chokepoint recovers the original plaintext.

**The core regression**: with the key ring lost, querying the account list and the backup configuration list still **succeeds**. That is the entire point of the change.

The gate holds: the scheduler skips tasks in `Lost` and logs one summary per tick; action endpoints return 409 `keyring_lost`. Resets do not persist on a failed verification, do persist on a successful one, and the verification path has no side effects. The status flips only when every record has been reset, not partway. The ordinary PUT rejects a non-empty password. `/api/health/ready` judges locally and returns 503 while `Lost`.

## 6. Deliberately not done

- No changing or migrating a backup password (which would mean re-encrypting historical packs)
- No "abandon history and use a new password" escape hatch (decision 6)
- No `ProtectedValue` wrapper type — the rename in decision 2 already makes misuse require a deliberate act, and the extra intrusion is not worth the marginal gain
- No persisted per-row degradation flag: `secretsUnavailable` and the pending count are computed at read time from actual decryptability (§3.3), adding no columns
