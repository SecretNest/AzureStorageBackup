/**
 * The backup scope rule set — a mirror of the backend's ScopeRuleSet.cs.
 *
 * Why duplicate it: going through the API would mean a round trip per checkbox, and clicking through
 * a tree means dozens of them. The cost is that the two implementations must agree — pinned by the
 * shared fixture shared/scope-rule-cases.json, which both sides' tests read.
 * **Changing behaviour here means changing the backend too**, and vice versa.
 *
 * Rule semantics: each rule is "path → included/excluded", decided by the longest matching prefix;
 * with no match at all, included. Two write invariants keep the set permanently minimal:
 *   1) each rule must disagree with its nearest ancestor — an agreeing one is redundant and is not persisted;
 *   2) writing a rule deletes every deeper rule having it as a strict prefix.
 */

/** Immutable. Every operation returns a new value; React re-renders on the identity change. */
export type ScopeRules = ReadonlyMap<string, boolean>

const normalize = (path: string): string =>
  path
    .replace(/\\/g, '/')
    .split('/')
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
    .join('/')

/** The prefix shared by every descendant of a directory (empty string for the root, otherwise "dir/"). */
const under = (dirPath: string): string => {
  const p = normalize(dirPath)
  return p.length === 0 ? '' : `${p}/`
}

/** Whether key lies strictly beneath prefix (excluding the directory prefix itself names). */
const isUnder = (key: string, prefix: string): boolean =>
  key.length > prefix.length && key.startsWith(prefix)

/** Ordinal order: an ancestor always sorts before its descendants (a strict prefix compares less than any extension), so normalisation takes one pass. */
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0)

const sorted = (rules: Map<string, boolean>): Map<string, boolean> =>
  new Map([...rules.entries()].sort((a, b) => ordinal(a[0], b[0])))

const lookup = (rules: ReadonlyMap<string, boolean>, path: string): boolean => {
  let p = normalize(path)
  for (;;) {
    const hit = rules.get(p)
    if (hit !== undefined) return hit
    if (p.length === 0) return true // not even a root rule → include by default
    const slash = p.lastIndexOf('/')
    p = slash < 0 ? '' : p.slice(0, slash)
  }
}

/** Drop redundant rules in place (those agreeing with their nearest ancestor). */
const dropRedundant = (rules: Map<string, boolean>): Map<string, boolean> => {
  for (const key of [...rules.keys()].sort(ordinal)) {
    const self = rules.get(key)!
    rules.delete(key)
    if (lookup(rules, key) !== self) rules.set(key, self)
  }
  return sorted(rules)
}

/** Parse rule text. null/empty → include everything. Unrecognised lines are skipped, never thrown. */
export function parseScope(text: string | null | undefined): ScopeRules {
  const rules = new Map<string, boolean>()
  for (const raw of (text ?? '').split('\n')) {
    const line = raw.trim()
    if (line.length === 0) continue

    const included = line[0] === '+' ? true : line[0] === '-' ? false : null
    if (included === null) continue

    const path = normalize(line.slice(1))
    // A `..` segment can never match a real relative path; keeping it would only suggest it means something.
    if (path.split('/').some((seg) => seg === '..' || seg === '.')) continue

    rules.set(path, included)
  }
  return dropRedundant(rules)
}

/** Whether this is "include everything" (no rules at all). */
export const isAll = (rules: ScopeRules): boolean => rules.size === 0

/** Whether a path is in scope: longest prefix wins. */
export const isInScope = (rules: ScopeRules, path: string): boolean => lookup(rules, path)

/**
 * The indeterminate third state: a rule exists strictly beneath this directory, so the subtree is
 * divided.
 *
 * This is one-directional: with `- docs`, `+ docs/a` and `+ docs/b`, where docs contains only a and
 * b, everything is in fact selected and this still reports indeterminate. Without loading children
 * there is no way to know whether those two rules exhaust the directory — the inherent cost of lazy
 * loading. Indeterminate is the conservative and honest side; the backup result is unaffected, only
 * the display.
 */
export function isPartial(rules: ScopeRules, dirPath: string): boolean {
  const prefix = under(dirPath)
  for (const key of rules.keys()) if (isUnder(key, prefix)) return true
  return false
}

/** The checkbox's tri-state. **Computed from the rule set, never stored** — hence no parent/child propagation loop and no possibility of an infinite one. */
export const scopeState = (
  rules: ScopeRules,
  path: string,
): 'checked' | 'indeterminate' | 'unchecked' =>
  isPartial(rules, path) ? 'indeterminate' : isInScope(rules, path) ? 'checked' : 'unchecked'

/** Write one rule, maintaining both invariants, and return a new value. */
export function withRule(rules: ScopeRules, path: string, included: boolean): ScopeRules {
  const key = normalize(path)
  const next = new Map(rules)

  // Invariant 2: clear the deeper rules this one now covers.
  const prefix = under(key)
  for (const deeper of [...next.keys()]) if (isUnder(deeper, prefix)) next.delete(deeper)

  // Invariant 1: do not persist a rule that agrees with its nearest ancestor. Remove self first, and the nearest remaining match is the ancestor's decision.
  next.delete(key)
  if (lookup(next, key) !== included) next.set(key, included)

  return sorted(next)
}

/** Serialise back to text, one rule per line. An empty set → an empty string (stored as null, meaning "everything"). */
export const scopeToText = (rules: ScopeRules): string =>
  [...rules.entries()]
    .map(([key, included]) => (key.length === 0 ? (included ? '+' : '-') : `${included ? '+' : '-'} ${key}`))
    .join('\n')
