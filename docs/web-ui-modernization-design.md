# Web UI rework and container error handling

> The UI had no design: `index.css` and `App.css` were still leftover Vite scaffolding and were
> **in effect**, application styling was inline `style` scattered through 3,455 lines of JSX,
> every control had the browser default appearance, and several things broke in dark mode. This
> round establishes a design system and an app shell, aiming at the look of a compact operations
> console.
>
> It also fixes an unrelated defect: creating a container returned a bare 500 and the UI showed
> nothing but `ApiError: Internal Server error`.
>
> This round touches **visuals and layout skeleton only** — no interaction flow changes.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | Scope | **Visuals plus layout skeleton.** Establish a design system and rebuild the shell; interaction flows and information architecture inside pages stay as they are |
| 2 | Styling technology | **Hand-written CSS with zero runtime dependencies.** No Tailwind, no component library, no new entries in `package.json` |
| 3 | Visual direction | **Compact operations console** (the Linear / Vercel dashboard family). Hairline dividers instead of shadows, 4–6px radii, 14px body text, a neutral grey scale plus one accent |
| 4 | Dark mode | **Follows the system**, both token sets implemented, no manual toggle |
| 5 | Style organisation | **Global semantic classes.** Element selectors do the groundwork (`button` / `input` / `select` / `table` work without any class), with a few semantic classes on top. No CSS Modules, no TypeScript style-constant objects |
| 6 | Form field widths | **Tiered by content length.** Blob endpoint, account key, proxy host and local paths take the wide tier |
| 7 | The container 500 | **Catch `RequestFailedException` per endpoint**, no global exception handler — the existing decision not to take over global exception handling still stands |
| 8 | Frontend testing | **No test framework introduced this round.** Visual regression is checked by hand, page by page; a known limitation |

## 2. Defect: creating a container returned 500

### 2.1 Root cause

Three layers, each confirmed:

1. **Trigger**: the entered container name violated Azure's naming rules and Azure returned `400 InvalidResourceName`.
2. **Backend**: the create endpoint did not catch `RequestFailedException`, and the project deliberately has no global exception handler, so Azure's 400 bubbled all the way up and Kestrel turned it into a bare 500. The GET and DELETE endpoints had the same gap.
3. **Frontend**: the containers page validated nothing before sending the name to the cloud, and the API client fell back to `res.statusText` when the response body was empty — producing `ApiError: Internal Server error`, a string that says neither what is wrong nor what to change.

### 2.2 The fix

**Backend**

A static `ContainerName.Validate(name)` returns a description of the violation or `null`, implementing Azure's rules: 3–63 characters, lowercase letters, digits and hyphens only, first and last character alphanumeric, no consecutive hyphens.

The POST endpoint validates **before reaching the cloud** and returns 400 naming the specific rule broken. The reasoning: local validation can produce an actionable message, whereas Azure's "contains invalid characters" names neither the character nor the rule.

All three endpoints catch `RequestFailedException`:

- `Status` in 400–499 → pass the status code through, with the message carrying the `ErrorCode` and Azure's own description
- Everything else (including `Status == 0` connection failures, and 5xx) → mapped to **502**, meaning the storage account is unreachable

Response bodies use the project's existing `new { error = "…" }` shape.

Catching per endpoint rather than registering a global handler: a global handler would also take over every unhandled exception outside this round's scope, changing existing failure semantics.

**Frontend**

- The containers page validates with a TypeScript implementation equivalent to the backend's (the rules are implemented on both sides — backend authoritative, frontend for immediate feedback), disables the create button while invalid, and keeps the rules visible below the field.
- The API client parses the project's existing error shape `{ error, code? }`, using `error` as the message and preserving `code`, falling back to the raw text and `statusText` only when that fails. **This applies to every error message in the app**, not just containers.
- The delete call encodes the container name into the URL.

## 3. Design foundations

### 3.1 Cleanup

All of the following was deleted — Vite scaffolding, with the `#root` rule actively interfering with real layout: the `#root` width/centring/border rules, the 18px base font, the `.counter` class and template colour variables in `index.css`; the whole of `App.css`; the template images; and the template's social icon set, which nothing referenced.

The favicon was the Vite lightning bolt and was replaced with a simple mark matching `--accent`.

### 3.2 Tokens

Light values on `:root` in `index.css`, dark overrides under `@media (prefers-color-scheme: dark)`.

| Group | Variables |
|---|---|
| Surfaces | `--bg` canvas, `--bg-subtle` sidebar and table headers, `--bg-raised` cards and dialogs |
| Borders | `--border`, `--border-strong` |
| Text | `--text`, `--text-muted`, `--text-faint` |
| Accent | `--accent`, `--accent-hover`, `--accent-fg`, `--accent-subtle` |
| Semantic | `--ok` / `--warn` / `--danger`, each with `-bg` and `-border` variants |
| Type | `--font-sans` system stack, `--font-mono`; body 14px/1.5, h1 20px/600, h2 16px/600, secondary text 12px |
| Spacing | `--sp-1` … `--sp-6` = 4 / 8 / 12 / 16 / 24 / 32 |
| Radii | `--r-sm` 4px, `--r-md` 6px, `--r-lg` 8px |
| Shadow | `--shadow-overlay` only, for dialogs and floating layers |

**Flat regions never take a shadow.** That is the dividing line between "compact console" and "card-based SaaS", and it is the operative point of this visual direction.

Endpoints, paths, container names and hashes always use `--font-mono` — monospacing makes a marked difference to readability for that kind of content.

### 3.3 Focus and accessibility

One `:focus-visible` treatment throughout: a double ring (inner in `--bg` for separation, outer in `--accent`) covering buttons, inputs, links and inline table actions. Disabled states uniformly reduce opacity and set `cursor: not-allowed`.

## 4. The app shell

It used to be a row of bare `<button>` elements acting as tabs. Now:

- **A fixed 220px sidebar on the left**: product name at the top, eight navigation items in the middle, `Log out` at the bottom. The selected item gets a 2px `--accent` bar on its left edge and a deeper background.
- **Content on the right**: one page header (h1 left, primary action right) above the page body. Content is capped at 1280px wide with 24px horizontal padding.
- **The keyring banner** sits at the very top of the content area, above the page header, so it is visible first on any page.
- **Narrow screens (< 900px)**: the sidebar collapses into a horizontally scrolling tab strip at the top. Pure CSS media queries; React component structure and state logic are unchanged.
- **The login page** is rebuilt with the same tokens as a centred card.

The navigation data structure and the switching logic are untouched — only the appearance changes.

## 5. The component layer

Element selectors do the groundwork, so most places need no `className` at all.

**Buttons**: the default is the secondary style (outline plus `--bg-subtle`). Semantic classes are `.btn-primary` (solid `--accent`), `.btn-danger` and `.btn-ghost` (inline table actions, borderless).

**Inputs**: `input` / `select` / `textarea` share height, radius, border and focus ring; placeholders use `--text-faint`.

**Width tiers**: `.w-sm` 160px, `.w-md` 280px, `.w-lg` 480px, `.w-full`. Blob endpoint, account key, proxy host and local paths take `.w-lg` or `.w-full`.

**Deduplicating `Field`**: there were **four** different implementations, with label widths of 200 / 140 / 200 / 130 and alignments of center / center / flex-start / flex-start. They collapse into the single implementation in the modal component, laid out as `grid-template-columns: 200px 1fr` with `align-items: start` (which is correct for both checkboxes and multi-line controls). The other three are deleted and replaced with imports.

**Tables**: headers in `--bg-subtle` at 12px with increased letter spacing; rows highlight on hover; cell padding 8px / 12px; 1px `--border` between rows. One `.empty-state` presentation for empty lists.

**Dialogs**: the styles hard-coded `background: '#fff'`, which mismatched borders and inner controls in dark mode. They use `--bg-raised` and `--shadow-overlay`, with a light blur on the backdrop.

**Banners**: `.alert` plus `.alert-warn` / `.alert-error` / `.alert-ok`, replacing hard-coded hex colours in the keyring banner.

**Status badges**: `.badge` with semantic colours, used by the status columns on the tasks and backups pages.

## 6. Scope boundaries

Explicitly not done, to hold the line: no splitting up the large page components, no new dashboard page, no interaction flow changes, no frontend router, no frontend test framework.

## 7. Known limitations

There is no component-rendering test coverage. The project runs vitest over pure functions in `src/lib/` and `src/constants/`, but has no testing-library or jsdom, so anything that requires rendering — and all visual regression — is checked by hand across light, dark and narrow layouts. Say so plainly when delivering; do not describe it as "verified".

> **A trap this system has sprung three times**: overriding a rule in `index.css` requires counting specificity against *every* rule in the group being overridden, not just the obvious one. Element-selector groundwork means a new rule frequently competes with something written far away, and a rule that appears to lose has, each time, turned out to be losing to a selector nobody thought to check.
