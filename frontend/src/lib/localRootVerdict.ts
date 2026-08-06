// 迁移本地根路径的判定结果 → 界面决策（设计 docs/change-local-root-design.md）。
// 抽成纯函数是为了能测：仓库没有组件渲染测试的基建，对话框只负责把这里的输出画出来。

export interface LocalRootPreview {
  verdict: string // 'Ok' | 'NeedsConfirm' | 'Rejected' | 'NoBaseline' | 'BaselineUnreadable'
  sampled: number
  matched: number
  missing: number
  sizeMismatch: number
  mtimeDiffers: number
  matchRate: number
  reason: string | null
  examples: string[]
}

export interface LocalRootDecision {
  /** 当前是否已经可以点 Apply（needsForce 为真时还要求用户勾过复选框）。 */
  canApply: boolean
  /** 是否必须先手动勾选 "change anyway" 才能 Apply。 */
  needsForce: boolean
  tone: 'ok' | 'warn' | 'danger' | 'info'
  headline: string
}

export function localRootDecision(preview: LocalRootPreview | null): LocalRootDecision {
  if (!preview) {
    return { canApply: false, needsForce: false, tone: 'info', headline: 'Check the new path first.' }
  }

  switch (preview.verdict) {
    case 'Ok':
      return {
        canApply: true,
        needsForce: false,
        tone: 'ok',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match.`,
      }
    case 'NoBaseline':
      return {
        canApply: true,
        needsForce: false,
        tone: 'info',
        headline: preview.reason ?? 'No previous version to compare against — only the path itself was checked.',
      }
    case 'NeedsConfirm':
      return {
        canApply: false,
        needsForce: true,
        tone: 'warn',
        headline: `Only ${preview.matched} of ${preview.sampled} sampled entries match.`,
      }
    case 'Rejected':
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match — this looks like the wrong directory.`,
      }
    case 'BaselineUnreadable':
      // 有历史却读不出来，和「压根没有历史」是两回事：后者放行，前者必须问一句。
      // reason 里带着底层异常原文——用户在 NAS 上没有命令行，那是他唯一的诊断线索。
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: preview.reason ?? 'This backup has history, but its latest version index could not be read.',
      }
    default:
      // 后端加了新 verdict 而前端还没跟上时，宁可卡住也不放行。
      return { canApply: false, needsForce: false, tone: 'danger', headline: 'Unrecognised check result.' }
  }
}
