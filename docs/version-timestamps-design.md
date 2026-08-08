# Version timestamps (start / end)

## The problem

The version dropdown in the restore dialog showed nothing but a bare number (`1`, `2`, `3`). An operator picking "the backup from last Thursday" gets no help at all from a number — they need the moment each version corresponds to. The completion notice had the same gap: `Completed — version 3`, with no indication of how long it ran or when it finished.

## Starting point

- `BackupVersion.CreatedAt` (UTC) already existed, meaning **the moment the version was committed**, i.e. when the backup ended. The versions endpoint already returned it; `RestoreDialog.tsx` threw it away at `versions.map(v => v.version)`.
- **The start time was persisted nowhere.** `BackupRunState` had no time fields either.
- Times are stored as `DateTimeOffset.UtcNow` and rendered with `toLocaleString()` — already "store UTC, display in the client's zone". This design keeps that and introduces no timezone configuration.

## Design

### 1. Data model: `BackupVersion.StartedAt`

```csharp
/// The moment this backup started running (UTC). Versions written before the upgrade have none → null.
public DateTimeOffset? StartedAt { get; init; }
```

`CreatedAt` keeps its meaning (committed = ended). `StartedAt` is taken at the entry to `BackupOrchestrator.RunAsync`, before scanning begins.

### 2. Serialisation: `InfoFormat` 2 → 3

The serialiser always writes `StartedAt` (a nullable DTO) for version entries, and reads it as `format >= 3 ? ReadNullableDto(r) : null`. Old info files still read, with `StartedAt` null.

**The upgrade is one-way**: existing code throws `NotSupportedException` for `format > InfoFormat`, so once a newer build has written the info file, **an older image can no longer read it**. Fine for a rolling upgrade on a single NAS instance, but there is no rolling back to an older image afterwards.

### 3. The completion notice and the restore dialog show the same two numbers

The completion notice deliberately does **not** use the runner's own clock. Post-run cleanup (retention, compaction) keeps going for a while after the version is committed, so the run's end time is several minutes later than the version record — which would make the notice say 14:47 while the restore dialog says 14:44 for the same backup. Both now display **the two times in the version record**:

- The run result carries `StartedAt` / `CompletedAt` — exactly the `StartedAt` / `CreatedAt` written into `BackupVersion`.
- The run state carries them too, filled in by the runner from the result.
- The API exposes them as `startedAt` / `completedAt` (ISO 8601 with `Z`).

### 4. Frontend

A shared formatting function lives in `frontend/src/constants/format.ts` (next to `formatBytes`), called from both places so the wording cannot drift:

```
Version 3 — 2026-08-02 14:03 → 14:47              same day: the end side omits the date
Version 3 — 2026-08-02 23:41 → 2026-08-03 05:12   across midnight: the end side spells it out
Version 2 — — → 2026-08-01 03:12                  no start time: an em dash
```

"Same day" is judged in the **client's local timezone** (convert both to local dates, then compare), not by UTC date — otherwise two backups that are plainly on the same local day print two dates just because they straddled UTC midnight.

The restore dialog's version state became an array of objects carrying the times, with `Latest` still first in the list. The `Completed` branch of the run status reads `Completed — version 3 (2026-08-02 14:03 → 14:47)`, keeping the existing "N file(s) could not be read" paragraph. Against an older backend that does not send the new fields, it degrades to showing the number alone without the parenthetical.

## Deliberately not done

- **No backfill of start times for historical versions.** The data does not exist, and a guessed number is worse than an empty one.
- **No duration** (`44m`): the two moments already give it, and a derived number would only make the line longer.
- **No timezone setting**: the browser's timezone is the operator's timezone.
