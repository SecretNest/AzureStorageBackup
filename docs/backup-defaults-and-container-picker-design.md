# Backup form: default inheritance and the container picker

> The new-backup form had two gaps, both on the same screen.
>
> **One: "use default" was never implemented.** PRD §3 requires that a backup ticking "use
> default" adopts those values, but the code copied **concrete numbers** out of the global
> defaults when opening the form. What got stored was a snapshot, so changing a global setting
> afterwards moved nothing. That was an implementation falling short of the PRD, not a new
> requirement.
>
> **Two: the container could only be typed.** The account was a dropdown while the container was
> a plain text field, even though the app already had an endpoint that lists containers. The
> create endpoint only checked non-empty — it neither verified existence nor created anything — so
> one typo silently stored a configuration that failed only once it ran.
>
> Supplements [product-requirements.md](product-requirements.md) §3 and §4.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | How the two relate | **One spec, one round**, but kept as separate task groups and separate commits so review and rollback stay separable |
| 2 | Inheritance granularity | **One checkbox per field.** A single switch for the whole configuration cannot express common combinations like "custom tier, inherited retention" |
| 3 | How inheritance is represented | **NULL means inherit, non-NULL means override.** One rule across all eleven inheritable fields, no exceptions, no sentinel values like -1 |
| 4 | The `VolumeBytes` conflict | That field used `null` to mean "volume splitting off", which collides with inheritance. **"Off" moves to `0`**, so `null` means the same thing on every inheritable field. The settings page already said `0 = off`, so the UI semantics were already there |
| 5 | The three rule fields' conflict | `null` = inherit, `''` = explicitly no rules. Verified that `string?` passes through from DTO to entity untouched, so the two are distinguishable |
| 6 | When resolution happens | **At use, never filled in at read time.** See §3.2 — filling in would make the feature cancel itself |
| 7 | Migrating existing configurations | **All treated as overrides**; all eleven fields keep their current concrete values, never silently converted to inheritance |
| 8 | `IndexTier` / `DataTier` | **Not inheritable.** The service refuses to change them after creation, and inheritance means exactly "follows the global setting" — i.e. a change after creation. They are pre-filled from the global defaults on the new form and fixed on save |
| 9 | Containers that already hold a backup | Flagged in the dropdown but not disabled. **Partly overturned on 2026-07-31** — see §2.3 |

## 2. The container picker

### 2.1 Behaviour

Once an account is chosen, the existing containers endpoint is called and its results fill the dropdown, with a fixed final entry `+ New container…`. Selecting that entry reveals a text field validated live by the existing name validator.

- **Flagging existing backups**: the endpoint returns a presence flag, rendered as `● has backup` after the option. Pointing at a container that already holds a backup is usually a mistake — that case wants the Import flow — but it is only a hint, not a block.
- **Flagging local occupancy** (added 2026-07-31): the endpoint also returns `inUseBy`, the name of the local backup configuration already holding that container. That option **is** disabled, reading `● in use by "<name>"`.
- **Changing account clears the selection**: otherwise a name belonging to the previous account lingers, and the backend does not verify existence.
- **Listing failures are survivable**: listing requires the cloud. On failure the dropdown degrades to a plain text field showing the reason — being unable to list must not prevent creating a backup.

In edit mode the field is already locked and is unaffected.

### 2.2 What was left alone

The backend was not changed to verify that the container exists. The orchestrator calls `CreateIfNotExistsAsync` on the first backup, so naming a container that does not exist yet is a supported, normal flow.

### 2.3 Revision, 2026-07-31: occupancy is judged locally, and duplicates are refused outright

Decision 9's original reasoning was that the local database had no uniqueness constraint on `(accountId, container)`. It has one now, and the reasoning expired with it.

What overturned it was a real incident: **while a first backup was halfway through, that same container showed up in the dropdown as empty.** The presence flag only looked at whether the cloud info file existed, and that file is the very last thing the orchestrator writes — so the container already held this run's uploaded data while the cloud carried no marker at all. Following that list, the user assigned it to a second backup as well. The two then wrote competing version histories over each other, and each one's data blobs were deleted as orphans by the other's retention cleanup.

- **Occupancy is authoritative locally**: the `BackupConfig` row exists from the moment it is created, without waiting for any cloud artefact. The containers endpoint merges in `inUseBy`, which covers all three cases at once: a backup in progress, a failed backup leaving a partial result, and the cloud being momentarily unreadable.
- **The server refuses hard**: both create and import look up `(accountId, container)` before writing and return 409 if taken. Import checks this *before* reading the cloud — a question the local database can answer should not cost a network round trip first, and an import destined for rejection should not seed the cloud info file into the tracked store on its way.
- **The database backs it up**: a unique index on `(AccountId, ContainerName)` closes both writes that bypass the endpoint and races squeezing into the check-then-write window.
- **The migration deletes nothing**: existing duplicates are moved aside — the lowest `Id` in each group is kept, and the rest have their `ContainerName` rewritten to a dotted name (which Azure never accepts, so it can never touch a real container again), are marked `Error`, and carry a reason. Nothing is deleted. A duplicate configuration points at real cloud data, and deleting the local record would only make that data invisible.

§2.2's "the backend was left alone" held only for the original round: the backend did change here, but it still does **not** verify cloud-side existence — the `CreateIfNotExistsAsync` reasoning still stands.

## 3. "Use default" inheritance

### 3.1 The fields

Thirteen `BackupConfig` fields have a `Default*` counterpart in the global settings. After auditing each for "can this change after creation?", **eleven are inheritable** and two are not.

The criterion is the update service's locked list (account, container, local root, both tiers, and the password). Only the two tiers fall inside those thirteen; the other eleven are already assignable from the edit page, so following a global change is no more dangerous than what the user can already do by hand.

**Not inheritable** (pre-filled at creation, fixed on save): `IndexTier` and `DataTier`, both refused after creation by the update path. These two rows show no checkbox and are labelled `locked after creation` instead — one form should not contain two checkboxes that behave differently and are distinguished only by their caption.

**Inheritable (eleven)**

| Field | Global default | Type change | How "explicitly none" is expressed |
|---|---|---|---|
| `MaxVersions` | `DefaultMaxVersions` | → `int?` | n/a |
| `MaxAgeDays` | `DefaultMaxAgeDays` | → `int?` | n/a |
| `RetentionMode` | `DefaultRetentionMode` | → `RetentionMode?` | n/a |
| `SingleFileThresholdBytes` | `DefaultSingleFileThresholdBytes` | → `long?` | n/a |
| `GroupCapBytes` | `DefaultGroupCapBytes` | → `long?` | n/a |
| `IncludeSymlinks` | `DefaultIncludeSymlinks` | → `bool?` | n/a |
| `VerboseLogging` | `DefaultVerboseLogging` | → `bool?` | n/a |
| `VolumeBytes` | `DefaultVolumeBytes` | already nullable | **`0` = splitting off** |
| `IgnoreRules` | `DefaultIgnoreRules` | already nullable | **`''` = no rules** |
| `DontCompressRules` | `DefaultDontCompressRules` | already nullable | **`''` = no rules** |
| `DontGroupRules` | `DefaultDontGroupRules` | already nullable | **`''` = no rules** |

Global settings with no per-backup counterpart are out of scope: the repack-download switches, upload and download concurrency, ephemeral log age, retry backoff and cap, the dead-weight threshold, the staging limit, and the processing attempt limit.

### 3.2 Resolution

`ResolvedBackupSettings` takes `(BackupConfig, GlobalSettings)` and produces the effective value of all eleven, every one non-null. One rule: `null` takes the global value, anything else takes the field. The two tiers bypass the resolver and are read straight from the configuration.

**Resolution must happen at use, and must not fill values in when the configuration is read.** Fill them in at read time and the edit screen can no longer tell "an inherited 100" from "a 100 I typed", so saving quietly converts inheritance into an override and the feature cancels itself.

Four paths go through the resolver: backup, check, cleanup (evaluating retention) and restore.

### 3.3 API shape

The response carries both:

- the raw fields (nullable), which the UI uses to decide each checkbox's state;
- an `effective` object (all non-null), which the UI shows read-only where a box is ticked.

On the request, the eleven inheritable fields become nullable, with `null` meaning "inherit". The two tiers stay non-null.

### 3.4 UI

One row per inheritable field, label unchanged on the left, "checkbox plus control" on the right. A `DefaultableField` component wraps the existing controls rather than rewriting the form.

- Ticked: hide the control and show the `effective` value as read-only grey text.
- Unticked: show the control, **pre-filled with the current effective value**, so "adjust slightly from the default" does not mean retyping.
- Re-ticked: the field returns to inheritance and the typed value is **discarded** (`null` is sent on save). The form keeps no hidden draft — keeping one would make what is displayed differ from what will be saved.
- After a global setting changes, ticked rows show the new value the next time the form opens, with no action required.

A new backup starts with all eleven **ticked**, which is what PRD §3 meant. The two tiers are pre-filled from the global defaults, editable, and fixed on save.

## 4. Migration

1. The eleven inheritable columns become nullable (SQLite rebuilds the table; EF Core handles it). The two tiers are unchanged.
2. Existing `NULL` in `VolumeBytes` is rewritten to `0`, preserving the original "splitting off" meaning (decision 4).
3. The other ten columns keep their values — every existing configuration is treated as an override (decision 7).

Silently converting existing configurations to follow the global settings would change the behaviour of already-running backups without the user knowing: a backup deliberately set to `MaxVersions=10` would suddenly become 100. A change like that has to be made by a person, explicitly.

## 5. Pinned behaviour

Each of the eleven fields resolves correctly on both the null and non-null path. The three-state cases hold: `VolumeBytes` distinguishes `null` (inherit), `0` (off) and a positive value, and each rule field distinguishes `null` (inherit), `''` (no rules) and content.

The migration is pinned: an old row with `NULL` in `VolumeBytes` must become `0`, not inheritance.

**The assertion that pins "follows rather than snapshots"**: `PUT` a field to `null`, change the global setting, and the `effective` value returned by a subsequent `GET` must change with it.

The locked-field regression still holds: a `PUT` carrying a tier different from the stored one must still be rejected — this round must not loosen that.

## 6. Known consequence: inheriting retention is destructive

`MaxVersions`, `MaxAgeDays` and `RetentionMode` being inheritable means that lowering the global `MaxVersions` from 100 to 10 causes every backup inheriting it to **actually delete the surplus versions at the next cleanup**.

That is the correct meaning of "follows the default", not a defect — the user could produce the same outcome backup by backup. But it turns one settings edit into a destructive operation across backups, with no indication of the blast radius on screen.

**Suggested**: show "N backups inherit this" beside each retention item on the settings page. One read-only line, sourced from the existing configuration list, needing no new endpoint.
