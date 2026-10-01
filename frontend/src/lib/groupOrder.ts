/**
 * A group's members as an ordered list of backup keys (see backupKey). The order is the group's run order:
 * a scheduled task runs the members first to last, so the list is edited as a sequence, not a set —
 * ticking a backup appends it, and the arrows move it. Both are pure so the arrows' edge cases
 * (first cannot go up, last cannot go down, a key not in the list is a no-op) are testable without a DOM.
 */
export function toggleMember(order: readonly string[], key: string): string[] {
  return order.includes(key) ? order.filter((k) => k !== key) : [...order, key]
}

export function moveMember(order: readonly string[], key: string, delta: -1 | 1): string[] {
  const from = order.indexOf(key)
  const to = from + delta
  if (from === -1 || to < 0 || to >= order.length) return [...order]
  const next = [...order]
  next.splice(from, 1)
  next.splice(to, 0, key)
  return next
}
