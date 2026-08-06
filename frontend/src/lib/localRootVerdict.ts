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
  /**
   * needsForce 为真时，复选框旁边要显示的正文——三个要求确认的 verdict 后果并不相同
   * （见下），所以放在这个纯函数里而不是让对话框自己判断 tone/verdict 去拼文案。
   * needsForce 为假时不设置。
   */
  confirmBody?: string
}

// NeedsConfirm 和 Rejected 都真的跑过逐文件比对，且确实有文件对不上——下次备份把它们
// 记成删除再重传是真实会发生的事。
const CONFIRM_BODY_COMPARED =
  'I understand — change it anyway. The next backup will record every file that no longer matches as deleted and upload the new ones. Scope rules are kept as they are and may no longer match if this directory is laid out differently.'

// BaselineUnreadable 完全没有比对过（Sampled/Matched/Missing 全是 0）——不是「文件对不上」，
// 是「这份备份自己的索引读不出来」。真正的后果是 TrackedInfoStore.LoadAsync 反序列化本地
// 缓存的索引字节时没有 try/catch：换根本身会成功，但索引仍然读不出来，下次备份大概率会
// 在同一处崩溃，这个操作并不会修好它。
const CONFIRM_BODY_UNREADABLE =
  "I understand — change it anyway. No comparison actually ran, because this backup's own index could not be read — nothing is known about whether these files match. The move itself will go through, but the unreadable index is a separate problem this operation does not fix, and the next backup is likely to fail on it until that's dealt with."

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
        confirmBody: CONFIRM_BODY_COMPARED,
      }
    case 'Rejected':
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match — this looks like the wrong directory.`,
        confirmBody: CONFIRM_BODY_COMPARED,
      }
    case 'BaselineUnreadable':
      // 有历史却读不出来，和「压根没有历史」是两回事：后者放行，前者必须问一句。
      // reason 里带着底层异常原文——用户在 NAS 上没有命令行，那是他唯一的诊断线索。
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: preview.reason ?? 'This backup has history, but its latest version index could not be read.',
        confirmBody: CONFIRM_BODY_UNREADABLE,
      }
    default:
      // 后端加了新 verdict 而前端还没跟上时，宁可卡住也不放行。
      return { canApply: false, needsForce: false, tone: 'danger', headline: 'Unrecognised check result.' }
  }
}
