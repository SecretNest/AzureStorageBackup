# Inline edit panels, and renaming the Tasks tab

> Two independent UX corrections, both reported from a phone.
>
> **One.** Pressing `Edit` on a backup opens a form that is always appended *below the table*. With
> a dozen backups on a phone that form lands several screens away from the row it belongs to, with
> nothing on screen connecting the two. The page already owns the right pattern for this — the
> running-status row (`tr.ops-row`) expands directly under its own card — the edit form simply never
> used it.
>
> **Two.** The bottom tab labelled `Tasks` is the vaguest word in the app. The page it opens is
> titled `Scheduled Tasks` and contains cron schedules; meanwhile the *Backups* page is where
> backups, restores and checks are actually running. A user looking for "what is running right now"
> reads `Tasks` and goes to the wrong place.
>
> No backend, API or type changes. Layout, one scroll behaviour, and UI wording only.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | Where does the edit form open? | **Inline, directly under the row being edited**, on desktop and phone alike. One code path, not a per-tier variant |
| 2 | What about `New Backup`? | **Stays below the table.** It belongs to no row, so it has no row to open under |
| 3 | Long form, distant buttons | **Already solved — the job is not to break it.** `.form-actions` is sticky on the phone tier today; moving the form into a `<td>` would silently disable it. See §4 |
| 4 | Scroll target after `Edit` | **The top of the row being edited**, not the top of the form — so "which one am I editing" and the start of the form are on screen together |
| 5 | Tab name | **`Schedules`**, not `Plans`. "Backup plan" conventionally means what/when/how-long-to-keep, and that configuration lives on the *Backups* page — `Plans` would collide with it. This page holds cron schedules only |
| 6 | The `Task type` field | **`Scheduled action`.** The page already spends `run` on execution (`Run now`, `Running…`, `Last run`), so any word near it reads as a live state. `Scheduled` fixes the word in the future tense |
| 7 | The cron column | **Stays `Schedule`.** Considered `When`; rejected as churn for its own sake |
| 8 | Backend naming | **Untouched.** `ScheduledTask`, `tasksApi` and `/api/tasks` all stay. Renaming them would spread across the backend and buys the user nothing visible |

## 2. Moving the form into the row

`BackupConfigsPage.tsx` currently renders the form as `{showForm && (<div className="panel">…)}`
after the table — roughly 490 lines of JSX (`983`–`1472`).

That JSX is **hoisted into a variable inside the component**, not extracted into a child component:

```tsx
const formPanel = showForm && (
  <div className="panel">…</div>
)
```

Extraction was considered and rejected. The form reads twenty-odd pieces of page state — `form`,
`step`, `editing`, `accounts`, `containerList`, `containerListError`, `scope`, `passwordConfirm`,
`newContainer`, `busy`, `error` and the setters for most of them. A child component turns every one
of those into a prop, which is a large diff, a permanent maintenance surface, and no behaviour
change to show for it. A variable moves the JSX without touching a single reference.

It is then rendered in one of two places, never both:

- **Editing** — inside `configs.map`, after the row (and after its `ops-row`, when that backup is
  also running):
  ```tsx
  {editing?.id === c.id && (
    <tr className="edit-row"><td colSpan={6}>{formPanel}</td></tr>
  )}
  ```
- **Creating** — `showForm && !editing`, at the existing position below the table.

The comment at `BackupConfigsPage.tsx:814` explaining that errors move inside the form because "the
form sits under the table" is now only half true, and is updated with it.

## 3. Joining the row to its form

`index.css` already solves this exact problem for `ops-row`, and the same three ingredients are
reused:

- the row above drops its bottom border — a new `is-editing` class, mirroring `has-ops`
- `tr.edit-row > td` drops its top padding
- on the phone tier the card's bottom corners square off and the expansion's top corners square off,
  so the two read as one card

The three-way case has to work as well: a backup that is **running and being edited** renders as
main row → `ops-row` → `edit-row`, and all three merge into a single card. The existing `has-ops`
rules cover the first seam; the new rules must not reintroduce a border at the second.

Note the hover-suppression group at the end of `index.css` — four selectors, three of which carry a
class. Anything added here has to be counted against them by hand; this file has already been caught
out by specificity three separate times, and each of those rules carries a comment saying so.

## 4. Keeping the sticky action bar alive

The form's action bar is **already sticky on the phone tier** — `index.css` inside the ≤640px
block, with a comment naming the new-backup wizard as the reason:

```css
.form-actions {
  position: sticky;
  bottom: calc(52px + env(safe-area-inset-bottom));   /* clears the bottom nav */
  z-index: 30;                                        /* below .sidebar's 40 */
  margin: var(--sp-4) calc(-1 * var(--sp-4)) 0;       /* span the panel's padding */
  padding: var(--sp-3) var(--sp-4);
  background: var(--bg-raised);                       /* content shows through otherwise */
  border-top: 1px solid var(--border);
}
```

So there is nothing to add here — but moving the form into a `<td>` **would silently switch it
off**, and that is the real work of this section.

`.table-scroll` is `overflow-x: auto`, and per CSS a non-`visible` overflow on one axis computes the
other to `auto` — so it is a scroll container on both axes, and a `sticky` descendant positions
against *it* rather than the viewport. Its height is its content height, so it never scrolls, and
the bar would just sit at its static position doing nothing. No error, no warning: a working feature
quietly stops working.

The fix is to drop the container on the tier where it is already redundant — carded tables are
block-level and cannot overflow horizontally:

```css
@media (max-width: 640px) {
  .table-scroll:has(table.cards) { overflow: visible; }
}
```

This also reaches the other carded tables (Accounts, Groups). That is a correction, not a side
effect: none of them can overflow horizontally in card form either. `:has()` is already used in this
file (`table.cards tr:has(td.empty-state)`), so it introduces no new baseline requirement.

**Why the phone tier only (≤640px), and not extended to the tablet tier (≤900px).** Sticky is
already scoped to ≤640px and stays there. The 900px tier keeps real tables, so `.table-scroll` is
still doing its job and cannot be switched off — a sticky bar there would be trapped by the exact
mechanism above. That tier also has no fixed bottom bar (the sidebar becomes a *top* strip), so the
`52px` offset would be wrong for it. It keeps today's non-sticky behaviour.

The tablet tier does inherit one new consequence: its form now sits inside `.table-scroll`, so if
the form turns out to be wider than the viewport there, the form itself scrolls horizontally rather
than the page. §6 measures this; the 700px screenshot is what catches it.

**One thing to re-check by eye, not by reasoning:** `.form-actions` uses a negative horizontal
margin to span its parent's padding. Its parent is about to change from a `.panel` sitting directly
in the page to a `.panel` nested in a table cell inside a card. If the bar no longer lines up with
the form's edges, the margin is what to adjust — the sticky behaviour itself is unaffected.

## 5. Scrolling to the form

`startEdit` records the id; an effect fires after the DOM commits and calls `scrollIntoView({ block:
'start', behavior: 'smooth' })` on the row being edited. Desktop gets this too — a long table puts
the row well below the fold there as well.

## 6. The table-width risk

On desktop the form now lives in a `<td colSpan={6}>`, which means its min-content width feeds into
the table's minimum width. That number is currently **656px**, and `index.css` spends three separate
rules getting it down there (header wrapping, `overflow-wrap: anywhere` on paths, dropping `nowrap`
on the actions column); a regression puts a horizontal scrollbar back under the whole table.

This is measured, not reasoned about. If the form's min-content exceeds 656px, `tr.edit-row > td`
gets shrink rules (the same `overflow-wrap: anywhere` the `ops-row` uses, plus capping the fixed
`w-lg` inputs at `max-width: 100%`) until it does not. Rolling back to a below-table form is not the
fallback.

**Measured 2026-08-14: it does not.** Headless Chrome against a static probe page, with the
expansion row present and then removed from the same DOM: **657px either way**. No shrink rules were
needed. Two reasons it lands this way — a `colspan` cell's min-content is shared across all six
columns rather than loaded onto one, and `.field` collapses to a single column below 900px, so its
200px label column never competes. (657 rather than 656 is the probe's own button text differing
slightly from the real page; what matters is that the two measurements are equal.)

One consequence worth knowing, unchanged by this work: at a ~700px window with the form open, the
page grows tall enough for a vertical scrollbar, whose 15px pushes the available width under 657 and
makes `.table-scroll` scroll horizontally. That is the backstop doing its job — the page itself
never scrolls sideways — and it happened the same way when the form sat below the table.

## 7. Renaming Tasks to Schedules

UI strings only. The tab key has no persistence anywhere — no `localStorage`, no URL hash, no
history entry — so renaming it carries no migration.

Located by string rather than by line, since these line numbers shift as the edit proceeds:

| File | From | To |
|---|---|---|
| `App.tsx` | `Tab` union member `'tasks'` | `'schedules'` |
| `App.tsx` | tab label `Tasks` | `Schedules` |
| `App.tsx` | `tab === 'tasks'` | `tab === 'schedules'` |
| `TasksPage.tsx` | `<h1>Scheduled Tasks</h1>` | `<h1>Schedules</h1>` |
| `TasksPage.tsx` | `New Task` (header button) | `New Schedule` |
| `TasksPage.tsx` | `<th>Type</th>` | `<th>Action</th>` |
| `TasksPage.tsx` | `data-label="Type"` | `data-label="Action"` |
| `TasksPage.tsx` | `No tasks yet.` | `No schedules yet.` |
| `TasksPage.tsx` | `{editing ? 'Edit Task' : 'New Task'}` | `'Edit Schedule' : 'New Schedule'` |
| `TasksPage.tsx` | `<Field label="Task type">` | `<Field label="Scheduled action">` |
| `TasksPage.tsx` | `Delete this task?` | `Delete this schedule?` |

The `data-label` and its `<th>` must move together — the card layout prints `data-label` via
`::before` as that cell's column name on phones, so leaving one behind gives a card that disagrees
with the table it came from.

Unchanged on purpose: the `Schedule` column (decision 7), `Last run`, `Run now`, and every
identifier — `TasksPage`, `tasksApi`, `ScheduledTask`, `taskTypeLabels`, `/api/tasks`. The filename
`TasksPage.tsx` stays too; renaming the file would churn imports for a name no user ever sees.

The `GroupsSection` embedded at the bottom of the page is unaffected. Groups exist only to be
targeted by schedules, which is why they are a section here rather than a tab, and that reasoning
survives the rename intact.

### Documentation

- `docs/mobile-adaptation-design.md:20,111` name the Tasks *table* as one of the two carded tables —
  updated to `Schedules`.
- `docs/web-ui-modernization-design.md:122` refers to "the tasks and backups pages" — updated.
- `docs/backup-feature-design.md`, `docs/m4-backup-engine-design.md`,
  `docs/product-requirements.md` and `docs/progress-display-design.md` say "scheduled tasks" about
  the *backend mechanism*, whose type is still `ScheduledTask`. Left alone; they are also records of
  decisions taken at the time, and rewriting them would falsify that record.
- `README.md` documents no UI navigation. Its one hit (`README.md:356`, `Scheduler__Enabled`)
  describes the backend scheduler and stays accurate. Not changed.

## 8. Verification

There is no component-test path for any of this, and none is being added. `vitest.config.ts` runs
`environment: 'node'` over `src/**/*.test.ts` — pure logic only, no jsdom, no `.tsx` — and the
mobile adaptation round already decided against introducing a component test framework. Adding
jsdom and a testing library to cover a layout change would be a far larger decision than the change
itself.

So layout claims here are settled with a browser, not by reading CSS — this repo has been wrong
about `index.css` specificity three times already.

- Headless Chrome screenshots at three widths: the phone tier (expanded form; scrolled to mid-form
  to confirm the sticky action bar still works — a regression check, it works today; the running +
  editing three-way merge), **1440px** (desktop inline expansion), and **700px** (the tablet tier,
  where sticky is deliberately off and the table form still applies).

  Note on the phone tier: headless Chrome clamps its viewport to a **500px** minimum, in both the
  old and new headless modes, so 390px cannot be measured directly. 500px sits in the same ≤640px
  tier — every rule involved matches identically — so the results carry, but the number in the
  screenshots is 500, not 390.
- Measure the Backups table's minimum width and confirm it has not moved past 656px (§6).
- `npx tsc -b` — **not** `tsc --noEmit`, which is a no-op in this project and passes unconditionally.
- The frontend test suite.
