import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { isAll, isInScope, isPartial, parseScope, scopeState, scopeToText, withRule } from './scopeRules'

// 与后端 ScopeRuleSetTests 读的是同一份文件。两份实现行为分叉时，两边同时红。
const fixture = JSON.parse(
  readFileSync(new URL('../../../shared/scope-rule-cases.json', import.meta.url), 'utf8'),
) as {
  queries: {
    name: string
    rules: string[]
    inScope: string[]
    outOfScope: string[]
    partial: string[]
    notPartial: string[]
  }[]
  writes: { name: string; start: string[]; ops: { path: string; included: boolean }[]; expect: string[] }[]
}

describe('shared fixture — queries', () => {
  for (const c of fixture.queries) {
    it(c.name, () => {
      const rules = parseScope(c.rules.join('\n'))
      for (const p of c.inScope) expect(isInScope(rules, p), `in scope: '${p}'`).toBe(true)
      for (const p of c.outOfScope) expect(isInScope(rules, p), `out of scope: '${p}'`).toBe(false)
      for (const p of c.partial) expect(isPartial(rules, p), `partial: '${p}'`).toBe(true)
      for (const p of c.notPartial) expect(isPartial(rules, p), `not partial: '${p}'`).toBe(false)
    })
  }
})

describe('shared fixture — writes', () => {
  for (const c of fixture.writes) {
    it(c.name, () => {
      let rules = parseScope(c.start.join('\n'))
      for (const op of c.ops) rules = withRule(rules, op.path, op.included)
      expect(scopeToText(rules)).toBe(c.expect.join('\n'))
    })
  }
})

describe('scopeState', () => {
  it('reports the three states off the rule set alone', () => {
    const rules = parseScope('-\n+ photos\n- photos/raw')

    expect(scopeState(rules, 'photos')).toBe('indeterminate')
    expect(scopeState(rules, 'photos/edited')).toBe('checked')
    expect(scopeState(rules, 'photos/raw')).toBe('unchecked')
    expect(scopeState(rules, 'music')).toBe('unchecked')
  })

  it('needs no knowledge of the file system', () => {
    // 整个功能的承重点：没有加载任何子节点，三态照样算得出来。
    const rules = parseScope('- docs\n+ docs/2026')

    expect(scopeState(rules, 'docs')).toBe('indeterminate')
    expect(scopeState(rules, 'never/loaded/anything')).toBe('checked')
  })
})

describe('parseScope', () => {
  it('treats null and empty text as "everything"', () => {
    expect(isAll(parseScope(null))).toBe(true)
    expect(isAll(parseScope(''))).toBe(true)
  })

  it('does not mutate the set it is given', () => {
    const original = parseScope('')
    const changed = withRule(original, 'photos', false)

    expect(isAll(original)).toBe(true)
    expect(isInScope(changed, 'photos')).toBe(false)
  })
})
