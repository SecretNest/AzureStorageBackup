# Local path root boundary and directory browsing

> The tool is deployed in Docker and backups necessarily operate on local paths, which until now
> could only be typed by hand into a web form. This adds two things: a directory/file tree browser
> in the UI, and a root boundary set by an environment variable — no local path operation (backup,
> restore, repair, browse) may cross it.
>
> The root is **a security filter only**: it does not rewrite paths, truncate them, or serve as the
> base for relative paths. Storage, display and logs all carry the full original path. With a root
> of `/nas`, `/nas/photos/2024` is displayed as `/nas/photos/2024`.
>
> Supplements [product-requirements.md](product-requirements.md). It also fixes a restore path
> traversal defect, §5.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | How the boundary is judged | **Resolve the real path segment by segment, then compare.** `GetFullPath` plus a prefix comparison does not stop symlinks — and "use symlinks to gather scattered directories into one place" is exactly the usage this feature is aimed at |
| 2 | With no root set | **No restriction**, behaviour identical to before. Browsing starts at `/` |
| 3 | Existing out-of-bounds configurations | **Keep the configuration, refuse to run it.** It still appears in the list, but backup / restore / check / repair all return 409. Startup is not blocked |
| 4 | When validation happens | **On every operation**, not only when settings are saved. A configuration may come from an older version, a hand-edited database, or `/import` |
| 5 | Out-of-bounds response | **409** with error code `path_outside_root`, and a message naming both the root and the rejected path |
| 6 | What browsing returns | Both directories and files; **only directories are selectable**. Out-of-bounds children are returned but flagged `outsideRoot`, greyed out and unclickable |
| 7 | Frontend shape | Keep the free-text field, add a `Browse` button beside it. Typed values go through the same validation |
| 8 | Restore path traversal | **Must be fixed independently of this feature.** Even with no root set, restore must never write outside `TargetRoot` |

## 2. Configuration

`Backup__Root` (configuration key `Backup:Root`, following the project's `Section__Key` convention).

- **Unset or empty** → no boundary, every local path allowed
- **Set** → the root's own real path is resolved once at startup and cached; every subsequent judgement uses that resolved root

Resolving the root itself first is mandatory: if `/nas` is itself a symlink to `/mnt/disk1`, comparing the literal string `/nas` against resolved real paths would reject every legitimate path.

## 3. Judging the boundary

### 3.1 Why it must be segment by segment

.NET has no `realpath`. `Directory.ResolveLinkTarget(path, returnFinalTarget: true)` resolves **only the last segment**: if `/nas/link` is a symlink to `/etc`, querying `/nas/link/passwd` returns `null`, because `passwd` itself is not a link. Relying on it alone misses every case where an **intermediate** segment is a symlink — which is precisely the shape most easily exploited.

So the expansion is implemented directly: start at the root, append one segment at a time, check whether that segment is a symlink, and if so substitute its target (which may be relative and needs normalising in place) before continuing.

### 3.2 The rules

1. Normalise the candidate with `Path.GetFullPath` (removing `..`, `.` and repeated separators).
2. Expand symlinks segment by segment to reach the real path.
3. Compare against the resolved root **on path segment boundaries**: `/nasty` must not pass by prefix-matching `/nas`. Equality with the root itself counts as inside.
4. Cap the depth for symlink cycles; exceeding it is judged out of bounds (not an exception, and not an infinite loop).
5. For a path that does not exist, judge its **nearest existing ancestor** — a restore target may be a directory that has not been created yet, and "does not exist yet" is not grounds for rejection.

## 4. Where validation happens

Every operation, not just settings:

| Site | What is validated |
|---|---|
| Creating a backup configuration | `LocalRoot` |
| Starting a backup / check / repair / cleanup | `config.LocalRoot` |
| Starting a restore | `TargetRoot`, including when it falls back to `config.LocalRoot` |
| The browse API | The requested `path`, and every child returned |

Validating only at the entry point is not enough: the boundary means "no configuration may cross it regardless of where it came from", and configurations can come from an older version, a hand-edited database, or an arbitrary container imported through `/import`.

## 5. Restore path traversal (an independent must-fix)

Restore combined `TargetRoot` with the entry path, and the conversion to a local path only substituted separators — it validated nothing about `..`. The entry path comes from **the cloud index**, so an entry containing `../../etc/cron.d/x` would be written outside `TargetRoot`.

This is not a theoretical risk: the `/import` endpoint accepts any container, so importing a backup of unknown provenance and restoring it can write a file anywhere inside the container.

**The fix**: validate that the combined result is still inside `TargetRoot`; an out-of-bounds entry is **skipped and counted in `FailedFiles`** rather than aborting the restore (consistent with the existing per-group tolerance). Symlink creation is treated the same way.

This is **independent of `Backup__Root`**: even with no root set, restore must never write outside `TargetRoot`.

## 6. The browse API

`GET /api/system/browse?path=...`, lazy, returning direct children only. The cloud version tree already works this way and the pattern is reused.

**Default `path`**: the root if one is set, otherwise the filesystem root (the deployment target is a Linux container, so `/`; on a development machine, the root of the current drive or volume).

With no root set, `outsideRoot` is always `false` — no boundary means no outside.

**The response** carries the current path, the parent path (stopping at the root) and the children. Each child has a name, full path, whether it is a directory, size and modification time, plus the `outsideRoot` flag.

**Why out-of-bounds children are returned rather than filtered out**: if `/nas/link → /etc` simply did not appear, the user would be confused about a directory entry they can plainly see elsewhere. Returning it with a flag explains why it cannot be used.

**Robustness**: a child that cannot be read (permissions, for instance) is skipped while the rest are still returned, so one failure does not fail the whole request. There is a cap on how many children come back in one response, so a directory with a hundred thousand entries cannot blow it up — and **truncation is stated explicitly**, never silently short.

## 7. Frontend

The local root field and the restore target field each gain a `Browse` button, sharing one path-browser dialog (breadcrumb, list, parent). The parent button stops at the root.

The text fields stay: typing is faster for someone who knows the path, and typed values go through the same validation, so it is not a bypass.

Files are shown in the list but are not selectable — both `LocalRoot` and `TargetRoot` are directories by definition, while being able to see files is how you confirm you picked the right place.

## 8. Pinned behaviour

Boundary judgement is the one place here where it is easy to write tests that look like they are testing something and are not. **Symlink cases must construct real links in a temp directory and must not be mocked** — the entire point of this feature is handling what the filesystem really does.

- `..` escapes are rejected.
- `/nasty` does not pass by prefix-matching `/nas`.
- A root that is itself a symlink still admits paths inside it.
- A path whose **intermediate** segment is a symlink pointing outside the root (`/nas/link/photos/a.jpg`) is rejected. This is the case `ResolveLinkTarget` alone would miss.
- A symlink cycle terminates and is judged out of bounds.
- A path that does not exist yet is judged by its nearest existing ancestor.
- An out-of-bounds configuration returns 409 from backup, restore, check and repair alike.
- Restore traversal: an index entry containing `../` is skipped and counted in `FailedFiles` while the rest restore normally — **and this holds with no root set as well**.
- With no root set, every path is allowed, browsing starts at `/`, and behaviour matches the previous release.
- The browse API returns 409 for an out-of-bounds `path`, flags out-of-bounds children, and states truncation when it truncates.

## 9. Deployment note

`Backup__Root` constrains paths **inside the container**, so it works together with volume mounts: mount every host directory you want to back up beneath that root. Unset means unrestricted. It only filters; it never rewrites, migrates or truncates paths in existing configurations, and the UI still shows full paths. Once set, existing out-of-bounds configurations are refused at run time while the configuration itself is preserved.

## 10. Deliberately not done

- The root is not a base for relative paths (paths are always complete)
- No multiple roots (one root plus volume mounts already covers "gather directories from several places")
- No pagination for browse results (a cap plus an explicit truncation notice instead)
- No selecting a file as a local root or restore target
