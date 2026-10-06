# Settings export and import — design

Date: 2026-10-06. Status: approved in conversation; implementation plan follows.

## Purpose

Move everything under the Settings page — Accounts, Backup defaults, Performance, Notifications — between
installs, or keep a copy of it, as one JSON file. Export and import both live on Settings → About. Backup
configurations, groups and schedules are out of scope: they reference accounts by id and have their own pages.

The file never carries ids. Accounts are matched on import by storage endpoint, so an import onto a database
that already has some of the accounts updates those rows in place and the backups, groups and schedules that
reference them keep working.

## File format

camelCase, the same shapes as the existing GET bodies for each section. No `id`, no `createdAt`.

```json
{
  "format": "azure-storage-backup-settings",
  "version": 1,
  "exportedAt": "2026-10-06T12:00:00Z",
  "includesSecrets": false,
  "accounts": [
    { "name": "...", "description": null, "blobEndpoint": "https://x.blob.core.windows.net",
      "region": 0, "accountKey": null, "useProxy": false, "proxyMode": 0,
      "proxyHost": null, "proxyPort": null, "proxyUsername": null, "proxyPassword": null }
  ],
  "backupDefaults": { "...": "same fields as GET /api/settings/defaults" },
  "performance":    { "...": "same fields as GET /api/settings/performance" },
  "notifications":  { "...": "same fields as GET /api/notifications" }
}
```

- `accountKey` / `proxyPassword` are plaintext when the export asked for secrets, `null` otherwise.
- On import every section (`accounts`, `backupDefaults`, `performance`, `notifications`) is optional. A missing
  section leaves that part of the database untouched, so a hand-trimmed file can import one part only.
- `format` must match exactly and `version` must be ≤ the current version (1); anything else is a 400.

## Backend

New `Services/SettingsTransfer.cs` (exporter + importer, one class), endpoints added to `SettingsEndpoints`.

### `GET /api/settings/export?includeSecrets=false`

Returns the document above as a file download: `Content-Disposition: attachment; filename=asb-settings-YYYYMMDD-HHMM.json`.
With `includeSecrets=true` and the keyring lost, returns the existing 409 `keyring_lost` payload (the secrets cannot
be decrypted; an export with `null` keys that claimed `includesSecrets: true` would be a lie).

### `POST /api/settings/import/preview`

Body: the document. Returns the plan without writing anything:

```json
{
  "accounts": [ { "name": "...", "blobEndpoint": "...", "action": "create" | "update", "needsAccountKey": true } ],
  "backupDefaults": true, "performance": true, "notifications": false
}
```

The three booleans say whether the section is present in the file (and so would be overwritten). Validation
failures are a 400 with a message: bad `format`/`version`, an account with a blank `name` or `blobEndpoint`,
two accounts in the file with the same normalised endpoint.

### `POST /api/settings/import`

Same body, same DTO. The frontend fills the keys the user typed into the matching accounts' `accountKey` /
`proxyPassword` before sending, so preview and import share one reconciliation routine. Returns the same plan shape
describing what was done.

### Reconciliation

- Match by normalised `blobEndpoint` (trailing `/` stripped, lower-cased) — the same rule `AccountService` already
  uses to reject duplicate endpoints; the normaliser is factored out and shared.
- Matched → update every non-secret field. `accountKey` non-empty → replace; empty/null → keep the stored
  ciphertext. `proxyPassword` likewise.
- Unmatched → create. `accountKey` empty/null → 400 listing the account names that need a key (preview tells the
  frontend in advance via `needsAccountKey`, so a normal flow never reaches this). `proxyPassword` empty → stored as
  null.
- Accounts in the database but not in the file are left alone. Ids never change.
- Settings sections are written whole through the existing `UpsertDefaultsAsync`, `UpsertPerformanceAsync` and
  `NotificationConfigService.UpsertAsync` paths.
- One database transaction; any failure rolls everything back. Serialised against concurrent account
  create/update/delete through `AccountTopologyGate`.

## Frontend — Settings → About

A new "Export / Import" section between System and Session.

**Export.** A checkbox "Include account keys and proxy passwords", off by default; when on, a warning line says
the file will contain plaintext secrets. "Export settings" fetches the endpoint through the API client (cookie
auth), turns the text into a Blob and triggers a download. Errors (e.g. 409 keyring lost) show next to the
button, which an `<a href>` download could not do.

**Import.** "Import settings…" opens a `.json` file picker. The file is parsed client-side (parse failure is
shown inline), sent to preview, and the plan opens in a Modal:

- a table of accounts: name, endpoint, Create / Update;
- three lines, Backup defaults / Performance / Notifications: "will be overwritten" or "not in file";
- for each `needsAccountKey` account, a password input "Account key" (required) and, when that account has
  `useProxy` and a `proxyUsername`, a "Proxy password" input (optional);
- "Import" is disabled until every required key is filled. On click the keys are written into the document's
  accounts and the import is posted. Success replaces the plan with a result summary (accounts created / updated,
  sections applied). Failure shows the error inside the dialog; the document stays, so the user can fix a key and
  retry.

After a successful import `SettingsPage` reloads its two halves (a reload counter passed to `useSettingsHalf`).
Accounts and Notifications fetch on mount when their tab is shown, so they are fresh anyway.

Pure logic — which accounts need a key, the summary wording — lives in `frontend/src/lib/settingsTransfer.ts`.

## Tests

Backend, `SettingsTransferTests` (service level, SQLite like `AccountServiceTests`):
- export without secrets → both secret fields null; with secrets → plaintext;
- import creates, updates and leaves accounts; ids of matched accounts unchanged;
- matched account with empty key keeps the stored ciphertext;
- new account without a key is rejected and nothing is written;
- preview writes nothing;
- a missing section leaves its rows untouched;
- wrong format / future version rejected;
- duplicate endpoint inside the file → whole import rolled back.

Endpoint tests: export with secrets while keyring lost → 409; Content-Disposition filename.

Frontend: vitest for `lib/settingsTransfer.ts`; no UI unit tests (project convention).

## Documentation

`docs/operations.md` gains "Settings export and import" (format, reconciliation, secrets handling);
`docs/web-ui.md` notes the About page entry. This spec is folded into those two files and `docs/superpowers/`
is deleted before the work is finished.
