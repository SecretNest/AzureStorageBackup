# Mobile adaptation: touch input and small screens

> The UI had a single 900px breakpoint doing three things: collapsing the sidebar into a top tab
> strip, making `.field` single-column, and widening dialogs. Those are **narrow-screen**
> adaptations; **touch** adaptation was essentially absent. Controls were 32px tall (below the
> 44px touch target), inputs were 14px (which makes iOS Safari zoom the whole page on focus),
> eight tables had no horizontal scroll container, and several interactions had only `:hover`.
>
> The goal is **fully operable on a phone** — including the two-step new/edit backup wizard and
> the restore dialog's directory tree — not merely "you can check status".
>
> No business logic or interaction flow changes; layout, hit areas and dialog structure only.

## 1. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | Target | **Fully operable.** Every page and every dialog can complete its task on a phone, with no "you have to go back to a computer for this" gap |
| 2 | Breakpoint strategy | **Size and input are orthogonal axes.** `max-width` governs layout (the 900px tablet tier stays, a 640px phone tier is added), `pointer: coarse` governs hit areas. Desktop mouse users see no change at all |
| 3 | Tables | **Primary tables become cards, secondary ones scroll horizontally.** Backups and Tasks turn each row into a card on the phone tier; Logs, Accounts, Containers, Groups and the restore version table get a horizontal scroll wrapper |
| 4 | Dialogs | **Full-screen panels on the phone tier**: fixed title bar on top, fixed action bar at the bottom, scrolling content between |
| 5 | Dialog structure | **A uniform header/body/footer**, not a phone-tier patch. Desktop appearance is unchanged |
| 6 | Primary navigation | **A fixed bottom tab bar on the phone tier.** `Log out` moves to the settings page, and the desktop sidebar loses it too, so both ends agree |
| 7 | Styling technology | **The existing conventions**: zero runtime dependencies, hand-written CSS, global semantic classes. No new packages |
| 8 | Frontend testing | No test framework introduced this round; see §8 |

## 2. Breakpoint strategy

The existing 900px breakpoint was carrying two different jobs: layout reflow (which should follow **size**) and interaction scale (which should follow **input method**). Conflating them produces errors in both directions — a desktop user narrowing their window suddenly gets chunky buttons, while a 1000px-wide touch tablet gets no enlarged hit areas.

Split into two orthogonal axes:

```
Size axis (max-width)                 Input axis (pointer)
├─ >900px  desktop: sidebar + tables  ├─ fine   (mouse) 32px controls, hover kept
├─ ≤900px  tablet:  top strip, 1 col  └─ coarse (touch) 44px controls, hover disabled
└─ ≤640px  phone:   bottom bar + cards + full-screen dialogs
```

The 640px choice: an iPhone 15 Pro Max in landscape (932px) lands on the tablet tier, not the phone tier. That is deliberate — in landscape there is enough width, and the table form is more efficient than the card form.

`pointer: coarse` is available in every modern browser; where it is not, it simply does not apply and the current behaviour results, so there is no regression risk.

## 3. Touch and input foundations

### 3.1 Control size

`--control-h` goes from 32px to 44px, **only under `pointer: coarse`**. Because every control references that variable (an existing convention of the design system), one change covers all of them.

### 3.2 iOS auto-zoom

On the phone tier, `input / select / textarea` go to **16px**. Below 16px, iOS Safari zooms the entire page the instant an input takes focus — **and does not zoom back out when it loses focus**. The user is left with a magnified interface they have to pinch back manually. This is a hard defect to fix, not a matter of taste.

The rest of the body text stays at 14px: only focused form controls trigger the behaviour.

### 3.3 Small hit areas

Some elements must keep their visual size (enlarging them would wreck the compact-console look) while their hit area grows to 44px. A pseudo-element does it, because a pseudo-element takes no part in layout and therefore pushes nothing aside:

```css
@media (pointer: coarse) {
  .icon-btn { position: relative; }   /* the pseudo-element's positioning context */
  .icon-btn::after {
    content: '';
    position: absolute;
    inset: 50% auto auto 50%;
    width: 44px; height: 44px;
    transform: translate(-50%, -50%);
  }
}
```

**The host must be `position: relative`**, or the pseudo-element spreads relative to some outer positioned ancestor and the hit area lands in the wrong place.

Applies to the collapse triangles, the restore dialog's tree disclosure triangle (18px wide), and inline `.btn-ghost` buttons in tables.

### 3.4 hover and active

On touch, `:hover` **sticks after a tap** until something else is tapped — a table row keeps a highlight that reads as "selected".

- Under `pointer: coarse`, the row hover background is disabled.
- Everything whose only feedback was `:hover` gains `:active`, so touch gets press feedback.
- `-webkit-tap-highlight-color: transparent` stops the system's default blue rectangle from covering the custom `:active`.

### 3.5 Safe areas

The viewport meta gains `viewport-fit=cover`, and both the bottom navigation bar and the full-screen dialog's action bar consume `env(safe-area-inset-bottom)` — otherwise the iPhone home indicator sits over the buttons.

### 3.6 Fixed widths

`.w-md` (280px) and `.w-lg` (480px) become `width: 100%` on the phone tier.

`.w-sm` (160px) **does not change**: that tier is all numeric fields — proxy port, version count, retention days, and the cron editor's inputs embedded mid-sentence in `at [__] h` and `min [__]`. Making those full-width is both ugly and pulls those labels apart. 160px fits even a 320px screen.

## 4. Primary navigation: a bottom tab bar on the phone tier

At ≤640px the shell becomes `grid-template-rows: 1fr auto`, with `order` moving the sidebar after the content and `position: fixed` pinning it to the bottom. The brand block hides (the page already has a title) and the nav items divide the width evenly.

The content area gains `padding-bottom` equal to the bar height plus the safe area, or the last row of content is permanently covered.

### 4.1 Where `Log out` goes

Four slots leave no room for a fifth entry, so `Log out` moves from the sidebar footer to the settings page.

The desktop sidebar loses it **as well** — no "on Settings on mobile, in the sidebar on desktop" fork. One function in two places is exactly the kind of thing that later maintenance forgets to keep in sync.

This means passing the logout logic down to the settings page, including the security consideration recorded in its comment (clear local state regardless of whether the server call succeeded), along with the "is auth required" flag. The logic itself moves unchanged.

## 5. Tables

### 5.1 Cards (Backups / Tasks)

On the phone tier, `table / thead / tbody / tr / td` become blocks, `thead` is hidden, each `tr` becomes a bordered card, and each `td` shows its field name on the left via `::before { content: attr(data-label) }`:

```
┌──────────────────────────────┐
│ Photos                       │  ← first column (name) enlarged and bold, no label
│ Account/Ctn  acct1 / photos  │
│ Local Root   /volume1/photo  │
│ Encrypted    Yes             │
│ Status       ● Idle          │
│ ──────────────────────────── │
│ [Back up] [Restore] [⋯]      │  ← action column, data-label left empty
└──────────────────────────────┘
```

CSS cannot reach the header text, so those two tables' cells need a `data-label` attribute — that is the entire JSX-side change in this section.

### 5.2 The run-status row

The Backups table puts a running backup/restore/repair/check status on its own row spanning every column. The reason is on record: the action column is `nowrap`, and a path of several hundred characters would stretch the table off screen.

Once cards are in play it naturally follows the card. But the existing "no divider between this row and the one above" rule depends on adjacency plus the table border model, which does not survive block layout: on the phone tier the preceding card drops its bottom border and the status row drops its top border, so the two merge visually into one card.

### 5.3 Horizontal scrolling (the other six tables)

Logs, Accounts, Containers, Groups, the restore version table and the backup detail table get a wrapper:

```css
.table-scroll { overflow-x: auto; -webkit-overflow-scrolling: touch; }
```

The container takes `tabindex="0"`, without which the overflow can only be reached by touch-dragging and keyboard users cannot scroll it (WCAG 2.1.1).

Logs are not turned into cards: "time / level / source / message" is a scan-oriented stream, and cards would fit only a handful per screen — worse than horizontal scrolling.

## 6. Dialogs: full screen on the phone tier

### 6.1 Starting point

The modal styles are just constant aliases for `.modal-overlay` / `.modal-panel`, and **every dialog shares one stylesheet**, which makes the CSS side of going full-screen very cheap.

### 6.2 A three-part structure

Dialogs gain a uniform structure, with desktop appearance unchanged:

```
.modal-panel
├─ .modal-header   title + close button
├─ .modal-body     content, overflow-y: auto
└─ .modal-footer   action buttons
```

On the phone tier the panel becomes `100vw` by `100dvh` with no border, radius or padding, laid out as `grid-template-rows: auto 1fr auto`, and the footer's bottom padding includes the safe area.

**`dvh`, not `vh`**: a mobile browser's address bar collapsing changes how `vh` resolves, so `vh` makes the bottom action bar jump during scrolling or hide behind the address bar.

### 6.3 Stacking nested dialogs

The restore dialog opens the path browser on top of itself. The overlay had a fixed `z-index: 50`, so two of them stacked worked only by accident of DOM order. On desktop the two panels are different sizes and the lower one's edge is visible, so the problem stays obscure; on the phone tier both are full screen, and getting the order wrong presents as "I tapped Browse and nothing happened".

The nested layer is now raised explicitly: the path browser uses `z-index: 60` as a second-level dialog. Its click-the-backdrop-to-close behaviour is kept, but on a full-screen phone dialog the backdrop is invisible and closing depends on the `✕` in the title bar — which is exactly why §6.2's header close button is required.

### 6.4 The wizard is not a dialog

The new/edit backup wizard is an inline panel that expands below the table and scrolls with the page, so it cannot take §6.2's full-screen structure.

It is also the single most critical thing for the "fully operable" goal: step 1's form is long, and on a phone you have to scroll a full screen to reach "next" at the bottom, and "back / save" after that.

Both button groups are wrapped in `.form-actions`, which on the phone tier is `position: sticky; bottom: 0` with a background and top border so content does not show through. The `bottom` offset has to clear the bottom navigation bar (§4), or the two bars overlap.

Sticky rather than converting the wizard into a dialog: the latter would disturb state management and where `showForm` renders, which is far more risk than the benefit, while keeping the buttons permanently visible achieves the same thing.

### 6.5 Background scroll locking

An open dialog locks body scrolling. Scroll chaining — reaching the end of the dialog's content and continuing to drag, which then moves the page behind it — is common on phones and makes people think the dialog closed.

A `useModalScrollLock()` hook adds `overflow: hidden` to `body` on mount, records the scroll position, and restores both on unmount, so the locking logic exists once and each dialog calls it once.

**The hook must be reference-counted**: the restore dialog opens the path browser on top of itself, and without counting, closing the inner one restores `body` overflow while the outer dialog is still open, letting the background scroll again. A module-level counter restores only when it reaches zero.

## 7. Per-page touch details

**Path browser**: directory rows were 32px-tall `btn-ghost` with 8px padding, hard to hit. They become full-width rows, minimum 44px tall, with a `›` indicator on the right. File rows (not clickable) are unchanged. Out-of-bounds items keep their greying and `title` hint — `title` does not appear on touch, but it was only ever supplementary, and unclickability is already expressed by `disabled`.

**Restore dialog**: the tree disclosure triangle's hit area grows to 44px, the fixed-width input becomes responsive, and the version table gets a scroll wrapper.

**Cron editor**: no vertical stacking needed. The simple-mode container already has `flexWrap: 'wrap'`, so three 160px numeric fields wrap naturally; only the advanced-mode container lacked `flexWrap`, where a full-width input pushes the Simple button off the edge.

**Notifications**: the fixed 200px event multi-select row becomes a responsive grid, single-column on the phone tier.

**`.field`** already goes single-column at 900px and needs no change.

## 8. How this is verified

Frontend testing runs vitest over the pure functions in `src/lib/` and `src/constants/`, but there is no testing-library or jsdom, so nothing that requires rendering can be asserted — and layout, hit areas and touch behaviour least of all. Verification is therefore `tsc -b`, `npm run build`, `npm run lint`, a page-by-page review against this document, and **the user testing it on a real phone**.

Playwright is not introduced: a viewport screenshot regression suite needs its baseline images maintained forever, and for a single-user tool that cost outweighs the benefit. This is a **known limitation**, consistent with the same judgement in [web-ui-modernization-design.md](web-ui-modernization-design.md).

## 9. Not done

- No changes to business logic, APIs or the data model
- No CSS framework or component library
- No gesture interactions (swipe to delete, pull to refresh) — they need extra discoverability design, and each has an explicit button equivalent
- No PWA or offline support
- No manual light/dark toggle (it continues to follow the system)
