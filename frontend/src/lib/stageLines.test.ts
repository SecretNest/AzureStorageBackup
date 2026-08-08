import { describe, expect, test } from 'vitest'

import type { StageProgress } from '../api/backupConfigs'
import { stageLines } from './stageLines'

function progress(over: Partial<StageProgress> = {}): StageProgress {
  return {
    stage: 'Uploading',
    processed: 0,
    total: 0,
    bytes: 0,
    currentItem: null,
    activeItems: [],
    bytesPerSecond: 0,
    preparing: 0,
    queued: 0,
    waitingOnArchive: 0,
    percent: null,
    etaSeconds: null,
    estimatedRemaining: null,
    workTotal: 0,
    workDone: 0,
    workRemaining: 0,
    transferredBytes: 0,
    unfinishedItemBytes: 0,
    stagedBytes: 0,
    checkingBytes: 0,
    transferTotal: 0,
    workPercent: null,
    spilledItems: 0,
    uploading: 0,
    waitingOnPeer: 0,
    waitingOnSlot: 0,
    checking: 0,
    ...over,
  }
}

describe('stageLines', () => {
  /**
   * 这是整段重排的理由。从前件数一行、字节一行，两行各自排各自的，于是屏幕上没有任何东西
   * 说得出 "+2.0 GB unfinished" 与 "100 MB ready to upload" 的先后——真有人拿这两个数
   * 追问了很久："既然卷与卷之间不会卡住，为什么它们同时非零却什么都没在传？"
   * 答案是它们根本不在时间轴的同一点上：前者的字节已经在云上，后者还没上路。
   * 把整条流水线按逆时间轴摆开，这个问题就不会再被问出来。
   */
  test('orders the whole pipeline from nearly-done back to queued', () => {
    const { pipeline } = stageLines(
      progress({
        unfinishedItemBytes: 3_000_000_000,
        activeItems: [
          { label: 'a', sent: 1, total: 2, percent: 50 },
          { label: 'b', sent: 1, total: 2, percent: 50 },
        ],
        waitingOnPeer: 2,
        waitingOnSlot: 3,
        stagedBytes: 2_800_000_000,
        checking: 1,
        checkingBytes: 100_000_000,
        preparing: 1,
        waitingOnArchive: 4,
        queued: 4_365,
        spilledItems: 12,
      }),
    )

    expect(pipeline.split(' · ')).toEqual([
      '+2.8 GB on the cloud',
      '2 volumes uploading',
      '2 objects waiting on the same content elsewhere',
      '3 volumes waiting for an upload slot',
      '2.6 GB ready to upload',
      '1 object checking files',
      '95.4 MB being checked',
      '1 object preparing',
      '4 objects waiting for the archive slot',
      '4,365 objects queued',
      '12 objects buffered to disk',
    ])
  })

  /** 为零的段整段消失——没有"0 objects queued"这种噪声。 */
  test('drops every empty segment', () => {
    const { pipeline } = stageLines(progress({ queued: 7 }))
    expect(pipeline).toBe('7 objects queued')
  })

  /**
   * 压完但还没核对完的那段，件数与字节各占一项且**互不重叠**：后端已经把 checkingBytes
   * 从 stagedBytes 里减出去了，前端只要不再把两者相加就行。同时出现时读起来是
   * "1 件在核对、那 95.4 MB 就是它的；另有 2.6 GB 已经核对完在等上路"。
   */
  test('keeps bytes being checked out of ready to upload', () => {
    const { pipeline } = stageLines(
      progress({ stagedBytes: 2_800_000_000, checking: 1, checkingBytes: 100_000_000 }),
    )
    expect(pipeline).toBe('2.6 GB ready to upload · 1 object checking files · 95.4 MB being checked')
  })

  /**
   * 一条流都没在传、手上却有活在压：这一刻速度是 0、在途列表空着，界面上看跟卡死一模一样。
   * 必须有一句话说出原因，而且它占的是 uploading 那个位置（时间轴上同一点）。
   */
  test('says why the wire is idle instead of just dropping the segment', () => {
    const { pipeline } = stageLines(progress({ preparing: 1, queued: 3 }))
    expect(pipeline).toBe('nothing on the wire right now · 1 object preparing · 3 objects queued')
  })

  /** 第一行只留已经落定的：完成度分数与真正传上去的量，在途的一律不进来。 */
  test('the done line carries only settled bytes', () => {
    const { done } = stageLines(
      progress({
        workTotal: 3_000_000_000_000,
        workDone: 1_900_000_000_000,
        workPercent: 62,
        transferredBytes: 1_900_000_000_000,
        unfinishedItemBytes: 3_000_000_000,
        stagedBytes: 2_800_000_000,
      }),
    )
    expect(done).toBe('1.7 TB / 2.7 TB original (62%) · 1.7 TB uploaded (100% of original)')
  })

  /**
   * 下载是另一个方向（拉下来 → 写出去），措辞不能跟上传共用；而且它的总量事先就知道
   * （索引里记着各卷尺寸）。还没恢复的那部分属于时间轴最靠前的一头，归第二行。
   */
  test('restoring reads in the download direction', () => {
    const { done, pipeline } = stageLines(
      progress({
        stage: 'Restoring',
        transferredBytes: 500_000_000,
        transferTotal: 2_000_000_000,
        workDone: 400_000_000,
        workRemaining: 1_600_000_000,
        activeItems: [{ label: 'a', sent: 1, total: 2, percent: 50 }],
      }),
    )
    expect(done).toBe('476.8 MB / 1.9 GB downloaded · 381.5 MB restored')
    expect(pipeline).toBe('1 object downloading · 1.5 GB to go')
  })
})
