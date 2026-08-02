/**
 * 备份范围的边界规则集 —— 后端 ScopeRuleSet.cs 的镜像实现。
 *
 * 为什么要重复一遍：走 API 意味着每点一个复选框就要一次往返，一棵树点几十下就是几十次
 * 请求。代价是两份实现必须行为一致 —— 由 shared/scope-rule-cases.json 这份共享夹具钉住，
 * 两边的测试读同一个文件。**改这里的行为就要同时改后端**，反之亦然。
 *
 * 规则语义：每条是「路径 → 包含/排除」，判定取最长匹配前缀那一条；一条都不匹配则包含。
 * 两条写入不变式让规则集永远最小：
 *   1) 每条规则的判定必须与最近祖先相反，相同即冗余、不落盘；
 *   2) 写入一条规则时删除所有以它为严格前缀的更深规则。
 */

/** 不可变。所有操作都返回新值，React 靠引用变化触发重渲。 */
export type ScopeRules = ReadonlyMap<string, boolean>

const normalize = (path: string): string =>
  path
    .replace(/\\/g, '/')
    .split('/')
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
    .join('/')

/** 该目录下所有后代共有的前缀（根为空串，其余为 "dir/"）。 */
const under = (dirPath: string): string => {
  const p = normalize(dirPath)
  return p.length === 0 ? '' : `${p}/`
}

/** key 是否严格位于 prefix 之下（不含 prefix 所指的目录本身）。 */
const isUnder = (key: string, prefix: string): boolean =>
  key.length > prefix.length && key.startsWith(prefix)

/** Ordinal 序：祖先必排在后代之前（严格前缀恒小于其扩展），规范化因此一遍即可。 */
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0)

const sorted = (rules: Map<string, boolean>): Map<string, boolean> =>
  new Map([...rules.entries()].sort((a, b) => ordinal(a[0], b[0])))

const lookup = (rules: ReadonlyMap<string, boolean>, path: string): boolean => {
  let p = normalize(path)
  for (;;) {
    const hit = rules.get(p)
    if (hit !== undefined) return hit
    if (p.length === 0) return true // 连根规则都没有 → 默认包含
    const slash = p.lastIndexOf('/')
    p = slash < 0 ? '' : p.slice(0, slash)
  }
}

/** 就地清掉冗余规则（判定与最近祖先相同者）。 */
const dropRedundant = (rules: Map<string, boolean>): Map<string, boolean> => {
  for (const key of [...rules.keys()].sort(ordinal)) {
    const self = rules.get(key)!
    rules.delete(key)
    if (lookup(rules, key) !== self) rules.set(key, self)
  }
  return sorted(rules)
}

/** 解析规则文本。null/空 → 全部包含。无法识别的行跳过而不抛。 */
export function parseScope(text: string | null | undefined): ScopeRules {
  const rules = new Map<string, boolean>()
  for (const raw of (text ?? '').split('\n')) {
    const line = raw.trim()
    if (line.length === 0) continue

    const included = line[0] === '+' ? true : line[0] === '-' ? false : null
    if (included === null) continue

    const path = normalize(line.slice(1))
    // `..` 段命中不了任何真实相对路径，留着只会让人以为它有意义。
    if (path.split('/').some((seg) => seg === '..' || seg === '.')) continue

    rules.set(path, included)
  }
  return dropRedundant(rules)
}

/** 是否「全部包含」（没有任何规则）。 */
export const isAll = (rules: ScopeRules): boolean => rules.size === 0

/** 某路径是否在范围内：最长前缀匹配。 */
export const isInScope = (rules: ScopeRules, path: string): boolean => lookup(rules, path)

/**
 * 三态里的「灰选」：存在以这个目录为严格前缀的规则，说明子树内部有分歧。
 *
 * 这是单向的：`- docs` + `+ docs/a` + `+ docs/b` 而 docs 下恰好只有 a、b 时，实际是全选，
 * 这里仍报灰选。不加载子节点就无从知道两条规则是否穷尽了目录 —— 懒加载的固有代价。
 * 灰选是保守且诚实的一侧，备份结果不受影响，只是显示。
 */
export function isPartial(rules: ScopeRules, dirPath: string): boolean {
  const prefix = under(dirPath)
  for (const key of rules.keys()) if (isUnder(key, prefix)) return true
  return false
}

/** 复选框的三态。**从规则集现算，不存** —— 因此没有父子传播回路，不可能死循环。 */
export const scopeState = (
  rules: ScopeRules,
  path: string,
): 'checked' | 'indeterminate' | 'unchecked' =>
  isPartial(rules, path) ? 'indeterminate' : isInScope(rules, path) ? 'checked' : 'unchecked'

/** 写入一条规则，维护两条不变式，返回新值。 */
export function withRule(rules: ScopeRules, path: string, included: boolean): ScopeRules {
  const key = normalize(path)
  const next = new Map(rules)

  // 不变式 2：清掉被这条覆盖的更深规则。
  const prefix = under(key)
  for (const deeper of [...next.keys()]) if (isUnder(deeper, prefix)) next.delete(deeper)

  // 不变式 1：与最近祖先判定相同则不落盘。先摘掉自身，剩下的最近匹配就是祖先判定。
  next.delete(key)
  if (lookup(next, key) !== included) next.set(key, included)

  return sorted(next)
}

/** 规范化文本，每行一条。空规则集 → 空串（存库时即 null，表示「全部」）。 */
export const scopeToText = (rules: ScopeRules): string =>
  [...rules.entries()]
    .map(([key, included]) => (key.length === 0 ? (included ? '+' : '-') : `${included ? '+' : '-'} ${key}`))
    .join('\n')
