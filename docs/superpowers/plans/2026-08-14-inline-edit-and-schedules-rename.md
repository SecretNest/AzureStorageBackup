# Inline Edit Panels and the Schedules Rename — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open the Backups edit form directly under the row it belongs to, and rename the misleading `Tasks` tab to `Schedules`.

**Architecture:** The edit form's 490 lines of JSX are hoisted into a variable inside `BackupConfigsPage` (not extracted into a component — it reads twenty-odd pieces of page state) and then rendered into an expansion row under the edited row, reusing the `tr.ops-row` pattern the page already owns. One CSS wrapper has to be switched off on the phone tier or an already-working sticky action bar silently dies. The rename is UI strings only.

**Tech Stack:** React 19, TypeScript, hand-written CSS (no framework, no runtime deps), Vite, vitest.

**Spec:** `docs/inline-edit-and-schedules-rename-design.md`

## Global Constraints

- **Branch:** work on `inline-edit-and-schedules-rename` (already created, spec already committed there). When done, merge to `main` with `--no-ff` and delete the branch. The repo keeps only `main`.
- **Everything written into the repo is English** — code comments, commit messages, docs. Conversation with the user stays Chinese.
- **Typecheck with `npx tsc -b`. Never `tsc --noEmit`** — it is a no-op in this project and passes unconditionally.
- **No component test framework exists and none is being added.** `vitest.config.ts` is `environment: 'node'` over `src/**/*.test.ts` — pure logic, no jsdom, no `.tsx`. Layout is verified with headless Chrome instead (see the recipe below). The 5 existing logic tests must stay green.
- **Breakpoint tiers:** `≤640px` = phone (cards, fixed bottom nav, sticky form actions); `641–900px` = tablet (top tab strip, real tables); `>900px` = desktop. `pointer: coarse` is a separate axis for hit areas.
- **Before adding any rule to `index.css`, count its specificity against the rules it must beat, by hand.** This file has been caught out three times; each of those rules carries a comment saying so. The hover-suppression group at the end of the file is four selectors, three of which carry a class.
- **Put measured numbers into CSS comments**, so whoever edits this next has the evidence rather than a guess.

### Headless Chrome recipe (used by several tasks)

Do not boot the app — replicate the DOM statically. What is being verified is CSS, not data.

```bash
# Screenshot
google-chrome --headless --disable-gpu --no-sandbox --allow-file-access-from-files \
  --screenshot=/tmp/shot.png --window-size=390,844 --virtual-time-budget=2000 \
  file:///tmp/probe.html
```

```bash
# Measure: the probe page writes numbers into document.title, then read them back
google-chrome --headless --disable-gpu --no-sandbox --allow-file-access-from-files \
  --virtual-time-budget=2000 --dump-dom file:///tmp/probe.html | grep -o '<title>[^<]*</title>'
```

Inside the probe page, measure with:
```js
document.title = JSON.stringify({
  overflow: document.documentElement.scrollWidth > window.innerWidth,
  scrollWidth: document.documentElement.scrollWidth,
  cols: [...document.querySelector('table').tHead.rows[0].cells].map(c => Math.round(c.getBoundingClientRect().width)),
})
```

The probe page must `<link>` the real `frontend/src/index.css` (not a copy) and reproduce the DOM that `BackupConfigsPage.tsx` emits.

---

### Task 1: Rename Tasks to Schedules

Fully independent of the rest of the plan — no layout risk, nothing else depends on it.

**Files:**
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/pages/TasksPage.tsx`
- Modify: `docs/mobile-adaptation-design.md`
- Modify: `docs/web-ui-modernization-design.md`

**Interfaces:**
- Consumes: nothing
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Rename the tab in `App.tsx`**

Three edits:
```tsx
type Tab = 'backups' | 'schedules' | 'logs' | 'settings'
```
```tsx
  { key: 'schedules', label: 'Schedules' },
```
```tsx
        {tab === 'schedules' && <TasksPage />}
```

The tab has no persistence anywhere — no `localStorage`, no URL hash, no history entry — so the key rename needs no migration. Leave the `TasksPage` import and component name alone.

- [ ] **Step 2: Rename the user-visible strings in `TasksPage.tsx`**

Located by string, since line numbers shift as you go:

| From | To |
|---|---|
| `<h1>Scheduled Tasks</h1>` | `<h1>Schedules</h1>` |
| `New Task` (the header button's text) | `New Schedule` |
| `<th>Type</th>` | `<th>Action</th>` |
| `data-label="Type"` | `data-label="Action"` |
| `No tasks yet.` | `No schedules yet.` |
| `{editing ? 'Edit Task' : 'New Task'}` | `{editing ? 'Edit Schedule' : 'New Schedule'}` |
| `<Field label="Task type">` | `<Field label="Scheduled action">` |
| `Delete this task?` | `Delete this schedule?` |

`<th>Type</th>` and `data-label="Type"` **must both change**: on the phone tier `table.cards td::before` prints `data-label` as that cell's column name, so changing one and not the other gives a card that disagrees with the table it came from.

Deliberately unchanged: the `Schedule` column header (the row already *is* a schedule, but `When` was considered and rejected as churn), `Last run`, `Run now`, and every identifier — `TasksPage`, `tasksApi`, `ScheduledTask`, `taskTypeLabels`, `/api/tasks`, and the filename `TasksPage.tsx`.

- [ ] **Step 3: Verify no user-visible "task" survives on that page**

Run:
```bash
cd frontend && grep -n "[Tt]ask" src/pages/TasksPage.tsx src/App.tsx
```
Expected: every remaining hit is an identifier (`TasksPage`, `tasksApi`, `ScheduledTask`, `TaskInput`, `TaskTargetKind`, `taskType`, `taskTypeLabels`), not a string rendered to the user.

- [ ] **Step 4: Typecheck and lint**

Run:
```bash
cd frontend && npx tsc -b && npm run lint && npm test
```
Expected: tsc silent, oxlint clean, 5 test files pass. If `tsc -b` reports an unused-variable or narrowing error on `Tab`, a `'tasks'` literal was missed.

- [ ] **Step 5: Update the two docs that name the UI**

In `docs/mobile-adaptation-design.md`, decision row 3 — `Backups and Tasks turn each row into a card on the phone tier` → `Backups and Schedules`. And the heading `### 5.1 Cards (Backups / Tasks)` → `### 5.1 Cards (Backups / Schedules)`.

In `docs/web-ui-modernization-design.md`, `used by the status columns on the tasks and backups pages` → `on the schedules and backups pages`.

Leave `backup-feature-design.md`, `m4-backup-engine-design.md`, `product-requirements.md` and `progress-display-design.md` alone — they say "scheduled tasks" about the backend mechanism, whose type is still `ScheduledTask`, and they are records of decisions taken at the time. `README.md` documents no UI navigation; its one hit describes the backend scheduler and stays accurate.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/App.tsx frontend/src/pages/TasksPage.tsx docs/mobile-adaptation-design.md docs/web-ui-modernization-design.md
git commit -m "$(cat <<'EOF'
refactor(ui): rename the Tasks tab to Schedules

Tasks was the vaguest word in the app. The page it opens is titled
"Scheduled Tasks" and holds cron schedules, while the Backups page is where
backups, restores and checks are actually running -- so someone looking for
"what is running right now" read Tasks and went to the wrong place.

Not Plans: "backup plan" conventionally means what/when/how-long-to-keep, and
that configuration lives on the Backups page, so Plans would have collided
with it.

The Task type field becomes Scheduled action rather than anything built on
run or action alone: this page already spends run on execution (Run now,
Running..., Last run), so a bare word next to those reads as a live state.

UI strings only. ScheduledTask, tasksApi, /api/tasks, taskTypeLabels and the
TasksPage filename are all untouched -- renaming them would spread across the
backend for a name no user ever sees.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Hoist the form JSX into a variable

A pure refactor with **zero visual change** — that is the whole point, and what makes it reviewable on its own.

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`

**Interfaces:**
- Consumes: nothing
- Produces: `formPanel` — a `false | JSX.Element` binding in `BackupConfigsPage`'s scope, defined before `return`. Task 3 renders it in a second location.

- [ ] **Step 1: Capture the block's content, whitespace-stripped**

A screenshot cannot verify this task — the probe pages in this plan are static HTML and do not reflect `.tsx` at all. What proves "nothing changed" is that the moved characters are the same characters:

```bash
cd frontend && sed -n '983,1472p' src/pages/BackupConfigsPage.tsx | tr -d ' \n' > /tmp/form-before.txt
wc -c /tmp/form-before.txt
```

Confirm the range is right before trusting it: line 983 should be `      {showForm && (` and 1472 should be its closing `      )}`.

- [ ] **Step 2: Move the JSX**

`BackupConfigsPage.tsx` currently holds, inside the returned tree (around lines 983–1472):

```tsx
      {showForm && (
        <div className="panel">
          …490 lines…
        </div>
      )}
```

Cut that whole block. Before the `return`, next to the existing `deleteButton` binding, add:

```tsx
  // The edit form is rendered in one of two places (see below): under the row being edited, or -- for a new backup, which
  // belongs to no row -- under the table. Holding it in a variable rather than extracting a component is deliberate: it
  // reads twenty-odd pieces of page state (form, step, editing, accounts, containerList, scope, passwordConfirm, ...), and
  // a child component would turn every one of them into a prop for no behaviour change whatsoever.
  const formPanel = showForm && (
    <div className="panel">
      …490 lines, dedented by 4…
    </div>
  )
```

Put `{formPanel}` where the block used to be. Dedent the moved JSX by 4 spaces (it goes from 6-space depth inside the tree to 2-space depth at statement level).

- [ ] **Step 3: Typecheck**

Run:
```bash
cd frontend && npx tsc -b && npm run lint
```
Expected: silent. A "used before declaration" error means `formPanel` landed after the `return` or after a binding it depends on — move it below the last state/`useMemo` it reads.

- [ ] **Step 4: Prove the moved JSX is character-identical**

Find the new line range of the block (from `const formPanel = showForm && (` to its closing `)`), strip whitespace the same way, and compare against Step 1:

```bash
cd frontend && sed -n '<new-start>,<new-end>p' src/pages/BackupConfigsPage.tsx \
  | sed '1s/.*constformPanel=showForm&&(/{showForm\&\&(/' | tr -d ' \n' > /tmp/form-after.txt
diff <(tail -c +20 /tmp/form-before.txt) <(tail -c +20 /tmp/form-after.txt)
```

Simpler and sufficient if that offset arithmetic is fiddly: compare the two files' byte counts and diff them directly, expecting the difference to be confined to the first line (`{showForm && (` became `const formPanel = showForm && (`) and the last (`)}` became `)`).

Expected: no differences in the body. Any difference in the middle means the move dropped or mangled something.

Also confirm the total line count of the file barely moved:
```bash
git diff --stat
```
Expected: roughly equal insertions and deletions, no net loss of ~490 lines.

- [ ] **Step 5: Commit**

```bash
git add frontend/src/pages/BackupConfigsPage.tsx
git commit -m "$(cat <<'EOF'
refactor(ui): hold the backup form in a variable

Pure move, no behaviour change: the form's JSX goes into a binding so the next
commit can render it in a second position. Extracting a component instead was
rejected -- the form reads twenty-odd pieces of page state, all of which would
have become props for no behavioural gain.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Render the form inline, and keep the phone tier intact

Ends with a working phone tier — which is why the `.table-scroll` fix cannot be split off into its own task: the moment the form enters a `<td>`, the existing sticky action bar dies.

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`
- Modify: `frontend/src/index.css`

**Interfaces:**
- Consumes: `formPanel` from Task 2
- Produces: `tr.edit-row` — the expansion row's class, used by Task 4's scroll target lookup and by the CSS in this task

- [ ] **Step 1: Render the expansion row**

Inside `configs.map`, after the `ops-row` block and before `</Fragment>`:

```tsx
                {/* The edit form expands under the row it belongs to, rather than at the bottom of the page: on a phone a
                    form appended below the table lands several screens from its own row, with nothing on screen connecting
                    the two. Same shape as the ops-row above. A new backup belongs to no row, so it keeps its place under
                    the table. */}
                {editing?.id === c.id && (
                  <tr className="edit-row">
                    <td colSpan={6}>{formPanel}</td>
                  </tr>
                )}
```

Then change the below-table render site from `{formPanel}` to:
```tsx
      {!editing && formPanel}
```

- [ ] **Step 2: Fix the now-stale comment**

The comment above `{!showForm && error && …}` explains that errors move inside the form because "the form sits under the table and this sits above it, so a save rejected by the server would show its reason several screens away from the button". Half of that is no longer true. Replace the reasoning with:

```tsx
      {/* While the form is open this moves inside it: the reason a save was rejected has to appear next to the button that
          was pressed, and this spot is above the table -- which is now potentially many rows away from the form. */}
```

- [ ] **Step 3: Join the row to its form (base tier)**

In `index.css`, next to the existing `tr.ops-row` rules:

```css
/* The edit form expands under the row it belongs to, and the row directly above it drops its bottom rule: the two are
   talking about the same backup. Found with :has() rather than a class on that row, because which row it is depends on
   state -- the main row when the backup is idle, the ops-row when it is running -- and one selector covers both where a
   class would need the branch spelled out in JSX. */
tbody tr:has(+ tr.edit-row) > td {
  border-bottom: none;
}
tbody tr.edit-row > td {
  padding-top: 0;
}
```

No hover rule is added for `.edit-row`: it holds a form, not a row of data, and highlighting it on hover would be noise. That also means the `pointer: coarse` hover-suppression group at the end of the file needs no new selector — there is no hover to suppress.

- [ ] **Step 4: Merge the card on the phone tier**

Inside the existing `@media (max-width: 640px)` block, next to the `tr.ops-row` card rules:

```css
  /* Same merge as the running-status row: the expanded form is part of the card above it, not a card of its own. With a
     backup that is both running and being edited, three rows merge into one card -- main row, ops-row, edit-row -- so this
     seam must not reintroduce what the ops-row rules just removed. */
  table.cards tr:has(+ tr.edit-row) {
    margin-bottom: 0;
    border-bottom: none;
    border-bottom-left-radius: 0;
    border-bottom-right-radius: 0;
  }
  table.cards tr.edit-row {
    border-top: none;
    border-top-left-radius: 0;
    border-top-right-radius: 0;
    padding-top: 0;
  }
  table.cards tr.edit-row td {
    display: block;
    padding-top: 0;
  }
  table.cards tr.edit-row td::before {
    content: none;
  }
```

And add `table.cards tbody tr.edit-row` to the existing background group (the one already listing `tbody tr`, `tr.has-ops`, `tr.ops-row`, `tr.has-ops:hover + tr.ops-row`) — that group exists to outrank the touch layer's hover suppression, and an edit row left out of it would drop its background after a tap.

- [ ] **Step 5: Keep the sticky action bar alive**

Still inside `@media (max-width: 640px)`:

```css
  /* Carded tables are block-level and cannot overflow horizontally, so this wrapper has no job on this tier -- and it is
     now actively harmful, because the edit form lives inside the table: overflow-x: auto computes overflow-y to auto as
     well, which makes this a scroll container, and .form-actions' sticky would position against it instead of the
     viewport and quietly stop working. No error, no warning -- just a feature that silently stops. */
  .table-scroll:has(table.cards) {
    overflow: visible;
  }
```

This also reaches the other carded tables (Accounts, Groups). That is a correction, not a side effect — none of them can overflow horizontally in card form either.

- [ ] **Step 6: Verify the sticky bar still works (regression)**

Probe page at 390px with the form expanded inside the table, scrolled to the middle of the form. Screenshot and look at it: the action bar must be at the bottom of the viewport, above the nav bar, with the form's content visible behind/above it.

If the bar is sitting mid-form instead, `.table-scroll` is still a scroll container — check that `:has(table.cards)` actually matched (the table carries both `cards` and `table-fluid`).

Also check the bar's horizontal alignment: `.form-actions` uses `margin: … calc(-1 * var(--sp-4)) …` to span its parent's padding, and its parent just changed from a page-level `.panel` to a `.panel` nested in a table cell inside a card. If the edges no longer line up, adjust that margin — the sticky behaviour itself is unaffected.

- [ ] **Step 7: Measure the desktop table's minimum width**

This is the risk the spec flags in §6. The form's min-content now feeds into the table's minimum width, which is currently **656px** and took three separate rules in `index.css` to get there.

Probe page at 1440px with the form expanded, then narrow the window in steps and read `document.documentElement.scrollWidth` via `--dump-dom` (see the recipe). Find the width at which the table stops shrinking.

Expected: still 656px. If it is larger, add shrink rules to `tr.edit-row > td` until it is not:
```css
tbody tr.edit-row > td {
  overflow-wrap: anywhere;
}
```
and, if the fixed-width inputs are the cause, cap them:
```css
tbody tr.edit-row .w-lg {
  max-width: 100%;
}
```
Rolling back to a below-table form is **not** the fallback. Write the number you measured into a CSS comment either way.

- [ ] **Step 8: Screenshot all three tiers**

- **390px** — expanded form merged into its card; and the three-way case (a backup that is running *and* being edited: main row → ops-row → edit-row as one card)
- **700px** — tablet tier: real table, form inline, sticky deliberately off. Confirm the form does not force horizontal scrolling inside `.table-scroll`
- **1440px** — desktop: form expands inline, table has no horizontal scrollbar

Read each PNG and look at it. Do not infer from the CSS.

- [ ] **Step 9: Typecheck, lint, test**

Run:
```bash
cd frontend && npx tsc -b && npm run lint && npm test
```
Expected: all clean, 5 test files pass.

- [ ] **Step 10: Commit**

```bash
git add frontend/src/pages/BackupConfigsPage.tsx frontend/src/index.css
git commit -m "$(cat <<'EOF'
feat(ui): open the backup edit form under its own row

The form was appended below the table, so on a phone -- where rows are cards --
it landed several screens away from the row it belongs to, with nothing on
screen connecting the two. It now expands directly under that row, reusing the
shape the running-status row already had. A new backup belongs to no row, so it
keeps its place under the table.

Moving the form into a <td> put it inside .table-scroll, which would have
silently killed the sticky action bar: overflow-x: auto computes overflow-y to
auto, making that wrapper a scroll container, and a sticky descendant positions
against it rather than the viewport. On the card tier the wrapper has no job
anyway -- carded tables are block-level and cannot overflow horizontally -- so
it is switched off there.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Scroll to the row on Edit

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`

**Interfaces:**
- Consumes: `tr.edit-row` rendering from Task 3
- Produces: nothing consumed later

- [ ] **Step 1: Add the ref and the trigger**

Near the other refs:

```tsx
  // Scrolling targets the row being edited, not the form: landing on the form's first field leaves "which backup am I
  // editing" off screen above. A state flag rather than a call inside startEdit, because the row has to be in the DOM
  // before it can be scrolled to.
  const editRowRef = useRef<HTMLTableRowElement | null>(null)
  const [pendingScrollToEdit, setPendingScrollToEdit] = useState(false)
```

At the end of `startEdit`, after `setShowForm(true)`:
```tsx
    setPendingScrollToEdit(true)
```

- [ ] **Step 2: Scroll once the row exists**

```tsx
  useEffect(() => {
    if (!pendingScrollToEdit) return
    setPendingScrollToEdit(false)
    editRowRef.current?.scrollIntoView({
      block: 'start',
      // Honour the OS setting: an unrequested smooth scroll is exactly what "reduce motion" is asking not to happen.
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    })
  }, [pendingScrollToEdit])
```

- [ ] **Step 3: Attach the ref to the row being edited**

On the main `<tr>` inside `configs.map`:
```tsx
                <tr
                  ref={editing?.id === c.id ? editRowRef : undefined}
                  className={ops.length > 0 ? 'has-ops' : undefined}
                >
```

- [ ] **Step 4: Typecheck and lint**

Run:
```bash
cd frontend && npx tsc -b && npm run lint && npm test
```
Expected: clean. If `editRowRef` errors on the `ref` prop, the generic is wrong — it must be `HTMLTableRowElement`, not `HTMLDivElement`.

- [ ] **Step 5: Commit**

```bash
git add frontend/src/pages/BackupConfigsPage.tsx
git commit -m "$(cat <<'EOF'
feat(ui): scroll to the row when its edit form opens

Targets the row rather than the form, so "which backup am I editing" and the
start of the form are on screen together. Desktop benefits too -- a long table
puts the row below the fold there as well. Falls back to an instant jump under
prefers-reduced-motion.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Final verification and merge

**Files:** none modified unless verification turns something up

- [ ] **Step 1: Full check**

Run:
```bash
cd frontend && npx tsc -b && npm run lint && npm test && npm run build
```
Expected: all pass, build succeeds.

- [ ] **Step 2: Confirm no Chinese text entered the repo**

Run:
```bash
git diff main...HEAD | grep -P '[\x{4e00}-\x{9fff}]'
```
Expected: no output.

- [ ] **Step 3: Re-read the diff against the spec**

Run `git diff main...HEAD` and check each spec section has a corresponding change: §2 the variable, §3 the seams, §4 the `.table-scroll` fix, §5 the scroll, §6 the measured width in a CSS comment, §7 every string in the rename table.

- [ ] **Step 4: Merge and clean up**

```bash
git checkout main
git merge --no-ff inline-edit-and-schedules-rename -m "$(cat <<'EOF'
Merge inline-edit-and-schedules-rename: inline edit panels, Schedules rename

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
git branch -d inline-edit-and-schedules-rename
git push origin main
```

- [ ] **Step 5: Report to the user**

Say plainly what was verified and how (which screenshots, which measured numbers), and what was not. Do not claim the phone behaviour is confirmed unless a screenshot was actually looked at.

Stop here. Releasing (version bump, `docker-publish`) is a separate decision and is not part of this plan.
