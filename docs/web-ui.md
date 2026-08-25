# Web UI

A compact operations console, hand-written, with zero runtime styling dependencies. It targets both a
desktop browser and a phone, and treats those as two orthogonal axes rather than one breakpoint.

## Foundations

| Decision | Conclusion |
|---|---|
| Styling technology | hand-written CSS, no Tailwind, no component library, no new packages |
| Visual direction | compact operations console — hairline dividers, 4–6px radii, 14px body, neutral greys plus one accent |
| Dark mode | follows the system, both token sets implemented, no manual toggle |
| Style organisation | global semantic classes over element-selector groundwork |
| Testing | vitest over pure functions only; no component rendering |

**Element selectors do the groundwork**, so `button`, `input`, `select` and `table` work with no class
at all, and a few semantic classes sit on top. No CSS Modules, no TypeScript style-constant objects.

### Tokens

Light values on `:root` in `index.css`, dark overrides under `@media (prefers-color-scheme: dark)`.

| Group | Variables |
|---|---|
| Surfaces | `--bg` canvas, `--bg-subtle` sidebar and table headers, `--bg-raised` cards and dialogs |
| Borders | `--border`, `--border-strong` |
| Text | `--text`, `--text-muted`, `--text-faint` |
| Accent | `--accent`, `--accent-hover`, `--accent-fg`, `--accent-subtle` |
| Semantic | `--ok` / `--warn` / `--danger`, each with `-bg` and `-border` |
| Type | `--font-sans`, `--font-mono`; body 14px/1.5, h1 20px/600, h2 16px/600, secondary 12px |
| Spacing | `--sp-1` … `--sp-6` = 4 / 8 / 12 / 16 / 24 / 32 |
| Radii | `--r-sm` 4, `--r-md` 6, `--r-lg` 8 |
| Shadow | `--shadow-overlay` only |
| Controls | `--control-h`, 32px by default and 44px under `pointer: coarse` |

**Flat regions never take a shadow.** That is the dividing line between "compact console" and
"card-based SaaS", and it is the operative point of this visual direction.

Endpoints, paths, container names and hashes always use `--font-mono` — monospacing makes a marked
difference for that kind of content.

One `:focus-visible` treatment throughout: a double ring, inner in `--bg` for separation and outer in
`--accent`, covering buttons, inputs, links and inline table actions.

> **A trap this system has sprung three times.** Overriding a rule in `index.css` requires counting
> specificity against **every** rule in the group being overridden, not just the obvious one.
> Element-selector groundwork means a new rule frequently competes with something written far away,
> and a rule that appears to lose has, each time, turned out to be losing to a selector nobody
> thought to check. There is a hover-suppression group at the end of the file — four selectors, three
> carrying a class — that anything new has to be counted against by hand.

## Two orthogonal axes

The original single 900px breakpoint was doing two different jobs: layout reflow, which should follow
**size**, and interaction scale, which should follow **input method**. Conflating them produces errors
in both directions — a desktop user narrowing their window suddenly gets chunky buttons, while a
1000px-wide touch tablet gets no enlarged hit areas.

```
Size axis (max-width)                 Input axis (pointer)
├─ >900px  desktop: sidebar + tables  ├─ fine   (mouse)  32px controls, hover kept
├─ ≤900px  tablet:  top strip, 1 col  └─ coarse (touch)  44px controls, hover disabled
└─ ≤640px  phone:   bottom bar + cards + full-screen dialogs
```

> **Rationale — why 640px.** An iPhone 15 Pro Max in landscape is 932px and lands on the tablet tier,
> not the phone tier. In landscape there is enough width, and the table form is more efficient than
> the card form.

`pointer: coarse` is available in every modern browser; where it is not, it simply does not apply and
the current behaviour results, so there is no regression risk.

## The shell

- **A fixed 220px sidebar on the left**: product name, navigation items, and the selected item marked
  with a 2px accent bar and a deeper background.
- **Content on the right**: one page header (h1 left, primary action right) above the body, capped at
  1280px with 24px horizontal padding.
- **The keyring banner** sits above the page header, so it is visible first on any page.
- **≤900px**: the sidebar becomes a horizontally scrolling tab strip at the top.
- **≤640px**: the shell becomes `grid-template-rows: 1fr auto`, with `order` moving the sidebar after
  the content and `position: fixed` pinning it to the bottom as a tab bar. The content area gains
  `padding-bottom` for the bar height plus the safe area, or the last row is permanently covered.

**`Log out` lives on the settings page**, on desktop as well as phone.

> **Rationale.** Four bottom-bar slots leave no room for a fifth entry. Making it "on Settings on
> mobile, in the sidebar on desktop" is exactly the kind of fork later maintenance forgets to keep in
> sync.

## Touch

**Control height** goes to 44px under `pointer: coarse` alone. Because every control references
`--control-h`, one change covers all of them.

**Inputs go to 16px on the phone tier.**

> **Rationale.** Below 16px, iOS Safari zooms the entire page the instant an input takes focus — **and
> does not zoom back out when it loses focus**. The user is left with a magnified interface they have
> to pinch back manually. This is a hard defect, not a matter of taste. The rest of the body text
> stays at 14px, since only focused form controls trigger it.

**Small hit areas grow without growing visually**, via a pseudo-element — which takes no part in
layout and therefore pushes nothing aside:

```css
@media (pointer: coarse) {
  .icon-btn { position: relative; }
  .icon-btn::after {
    content: ''; position: absolute; inset: 50% auto auto 50%;
    width: 44px; height: 44px; transform: translate(-50%, -50%);
  }
}
```

**The host must be `position: relative`**, or the pseudo-element spreads relative to some outer
positioned ancestor and the hit area lands in the wrong place.

**`:hover` sticks after a tap** on touch, until something else is tapped — a table row keeps a
highlight that reads as "selected". So row hover is disabled under `pointer: coarse`, everything
whose only feedback was hover gains `:active`, and `-webkit-tap-highlight-color: transparent` stops
the system's blue rectangle covering the custom `:active`.

**Safe areas**: the viewport meta carries `viewport-fit=cover`, and both the bottom bar and the
full-screen dialog footer consume `env(safe-area-inset-bottom)`, or the iPhone home indicator sits
over the buttons.

**Fixed widths** `.w-md` (280px) and `.w-lg` (480px) become full width on the phone tier. `.w-sm`
(160px) does **not**: that tier is all numeric fields — proxy port, version count, retention days,
and the cron editor's inputs embedded mid-sentence in `at [__] h`. Making those full-width is both
ugly and pulls the labels apart, and 160px fits even a 320px screen.

## Tables

**Primary tables become cards; secondary ones scroll horizontally.**

On the phone tier, Backups and Schedules turn `table / thead / tbody / tr / td` into blocks, hide
`thead`, make each row a bordered card, and print each cell's field name from a `data-label`
attribute:

```
┌──────────────────────────────┐
│ Photos                       │  ← first column enlarged and bold, no label
│ Account/Ctn  acct1 / photos  │
│ Local Root   /volume1/photo  │
│ Status       ● Idle          │
│ ──────────────────────────── │
│ [Back up] [Restore] [⋯]      │  ← action column, data-label left empty
└──────────────────────────────┘
```

CSS cannot reach the header text, which is why those cells need `data-label` — and **it must move
together with its `<th>`**, or a card disagrees with the table it came from.

Logs, Accounts, Containers, Groups and the restore version table get
`.table-scroll { overflow-x: auto }` instead. The container takes `tabindex="0"`, without which the
overflow can only be reached by touch-dragging and keyboard users cannot scroll it at all.

> **Rationale — why Logs are not carded.** "Time / level / source / message" is a scan-oriented
> stream, and cards would fit only a handful per screen — worse than horizontal scrolling.

**The run-status row** spans every column beneath its backup, because the action column is `nowrap`
and a path of several hundred characters would stretch the table off screen. On the phone tier the
preceding card drops its bottom border and the status row drops its top border, so the two merge
visually into one card.

## Dialogs

A uniform three-part structure, with desktop appearance unchanged:

```
.modal-panel
├─ .modal-header   title + close button
├─ .modal-body     content, overflow-y: auto
└─ .modal-footer   action buttons
```

On the phone tier the panel becomes `100vw` by `100dvh` with no border, radius or padding, laid out
as `grid-template-rows: auto 1fr auto`.

**`dvh`, not `vh`.** A mobile browser's address bar collapsing changes how `vh` resolves, so `vh`
makes the bottom action bar jump during scrolling or hide behind the address bar.

**Nested dialogs are raised explicitly.** The restore dialog opens the path browser on top of itself;
the overlay had a fixed `z-index`, so two stacking worked only by accident of DOM order. On desktop
the lower panel's edge is visible so the problem stays obscure; on the phone tier both are full
screen, and getting the order wrong presents as "I tapped Browse and nothing happened". The path
browser now uses a second-level z-index — and since a full-screen backdrop is invisible, closing
depends on the header's `✕`, which is exactly why that header is required.

**Background scroll is locked** while a dialog is open, because scroll chaining — reaching the end of
the content and continuing to drag, which moves the page behind it — makes people think the dialog
closed. The hook that does it is **reference-counted**: without counting, closing the inner dialog
restores `body` overflow while the outer one is still open.

## The inline edit panel

Pressing `Edit` on a backup opens its form **directly under that row**, on desktop and phone alike —
one code path, not a per-tier variant. `New Backup` stays below the table, because it belongs to no
row.

The form JSX is **hoisted into a variable inside the component**, not extracted into a child.

> **Rationale.** It reads twenty-odd pieces of page state — the form, the step, what is being edited,
> accounts, the container list and its error, scope, password confirmation, busy and error flags, and
> the setters for most of them. A child component turns every one into a prop: a large diff, a
> permanent maintenance surface, and no behaviour change to show for it. A variable moves the JSX
> without touching a single reference.

It is rendered in one of two places, never both: inside the row map for editing, or below the table
for creating. The row above drops its bottom border and the expansion drops its top padding, so the
two read as one unit — the same three ingredients the run-status row already uses. The three-way case
has to work too: a backup that is **running and being edited** renders as main row → status row →
edit row, and all three merge into a single card.

After pressing Edit, an effect scrolls **the top of the row being edited** into view, not the top of
the form, so "which one am I editing" and the start of the form are on screen together. Desktop gets
this too — a long table puts the row well below the fold there as well.

### Keeping the sticky action bar alive

The form's action bar is sticky on the phone tier, clearing the bottom navigation. Moving the form
into a `<td>` **would silently switch that off**.

> **Rationale.** `.table-scroll` is `overflow-x: auto`, and per CSS a non-`visible` overflow on one
> axis computes the other to `auto` — so it is a scroll container on both axes, and a `sticky`
> descendant positions against *it* rather than the viewport. Its height is its content height, so it
> never scrolls, and the bar would just sit at its static position doing nothing. No error, no
> warning: a working feature quietly stops working.

The fix is to drop the container on the tier where it is already redundant, since carded tables are
block-level and cannot overflow horizontally:

```css
@media (max-width: 640px) {
  .table-scroll:has(table.cards) { overflow: visible; }
}
```

This is scoped to the phone tier deliberately. The 900px tier keeps real tables, so `.table-scroll`
is still doing its job and cannot be switched off — a sticky bar there would be trapped by the exact
mechanism above — and that tier has no fixed bottom bar, so the offset would be wrong for it.

### The table-width risk

On desktop the form lives in a `<td colSpan={6}>`, so its min-content width feeds into the table's
minimum width. That number is 656px, and three separate rules in `index.css` exist to get it there.

**Measured, not reasoned about**: headless Chrome against a static probe page, with the expansion row
present and then removed from the same DOM — **657px either way**. No shrink rules were needed. Two
reasons it lands this way: a `colspan` cell's min-content is shared across all six columns rather
than loaded onto one, and `.field` collapses to a single column below 900px so its 200px label
column never competes.

One consequence worth knowing: at a ~700px window with the form open, the page grows tall enough for
a vertical scrollbar, whose width pushes the available space under 657 and makes `.table-scroll`
scroll horizontally. That is the backstop doing its job — the page itself never scrolls sideways.

## Naming

The bottom tab is **`Schedules`**, not `Tasks` and not `Plans`.

> **Rationale.** `Tasks` was the vaguest word in the app: the page it opened was titled "Scheduled
> Tasks" and held cron schedules, while the *Backups* page is where backups, restores and checks are
> actually running — so a user looking for "what is running right now" read `Tasks` and went to the
> wrong place. `Plans` was rejected because "backup plan" conventionally means what/when/how-long-to-keep,
> and that configuration lives on the Backups page.

The field labelled `Task type` is **`Scheduled action`**: the page already spends "run" on execution
(`Run now`, `Running…`, `Last run`), so any word near it reads as a live state, and "Scheduled" fixes
the word in the future tense.

**Backend naming is untouched** — `ScheduledTask`, `tasksApi` and `/api/tasks` all stay. Renaming
them would spread across the backend and buys the user nothing visible.

## Error messages

The API client parses the project's error shape `{ error, code? }`, using `error` as the message and
preserving `code`, falling back to raw text and `statusText` only when that fails.

> **Rationale.** Before this, an empty response body produced `ApiError: Internal Server error` — a
> string that says neither what is wrong nor what to change. It applies to **every** error message in
> the app.

Container names are validated on both sides — backend authoritative, frontend for immediate feedback
— against Azure's rules: 3–63 characters, lowercase letters, digits and hyphens, first and last
character alphanumeric, no consecutive hyphens. The POST endpoint validates **before reaching the
cloud**, because local validation can name the specific rule broken while Azure's "contains invalid
characters" names neither the character nor the rule.

Cloud errors are caught **per endpoint**, not by a global exception handler: a global handler would
also take over every unhandled exception outside that scope, changing existing failure semantics.
Status 400–499 passes through with Azure's code and description; everything else, including
connection failures, maps to **502** meaning the storage account is unreachable.

## Live probes in a form

The sentinel field asks the backend, as you type, whether the path is there — debounced 400 ms, with
the in-flight request aborted on every keystroke so a stale answer cannot land last and describe a
path already edited away.

It is the one field in the app where **absence is a normal value**, so the verdict is worded as an
observation rather than a judgement, and rendered amber rather than red: "not there right now" is not
something to fix if the disk is simply not mounted at this moment, and only the operator knows which
it is. The save is never blocked by it — refusing to save an absent sentinel would make the setting
impossible to enter exactly when it is needed. Only a genuine refusal (outside the configured root)
is red.

With the box empty the probe follows the local root instead, mirroring the backend's `SentinelGate`,
so the line describes the check that will really run rather than one the form invented.

## How this is verified

There is no component-rendering test coverage. vitest runs `environment: 'node'` over pure functions
in `src/lib/` and `src/constants/` — no jsdom, no `.tsx`.

So layout claims are settled **with a browser, not by reading CSS**. Headless Chrome screenshots at
three widths: the phone tier (expanded form, scrolled mid-form to confirm the sticky bar, and the
running-plus-editing three-way merge), 1440px, and 700px.

> Headless Chrome clamps its viewport to a **500px minimum** in both the old and new headless modes,
> so 390px cannot be measured directly. 500px sits in the same ≤640px tier and every rule involved
> matches identically, so the results carry — but the number in the screenshots is 500, not 390.

Type checking is **`tsc -b`**, never `tsc --noEmit`, which is a no-op in this project and passes
unconditionally.

> **Rationale — why Playwright is not introduced.** A viewport screenshot regression suite needs its
> baseline images maintained forever, and for a single-user tool that cost outweighs the benefit.
> This is a **known limitation**: say so plainly when delivering, and do not describe hand-checked
> visuals as "verified".

## Not done

- No frontend router, no dashboard page, no splitting of the large page components
- No CSS framework or component library
- No gesture interactions — each has an explicit button equivalent
- No PWA or offline support
- No manual light/dark toggle

## See also

- [progress-display.md](progress-display.md) — the two run lines and what they must never imply
- [configuration.md](configuration.md) — the form's fields, inheritance and the scope tree
- [operations.md](operations.md) — the login page and the keyring recovery banner
