/**
 * 一个版本的起止时刻。时间以 UTC 存储，这里按浏览器所在时区渲染。
 *
 * 同一个**本地**日期只写一次日期（判断也必须按本地时区做——按 UTC 判断的话，本地明明同一天
 * 的备份会因为跨了 UTC 零点被写成两个日期）；跨日则两侧都写全。升级前写下的版本没有开始
 * 时刻，写「—」，不拿别的时间冒充。
 */
export function formatVersionSpan(startedAt: string | null, completedAt: string): string {
  const end = new Date(completedAt)
  if (!startedAt) return `— → ${end.toLocaleString()}`
  const start = new Date(startedAt)
  const sameDay = start.toLocaleDateString() === end.toLocaleDateString()
  return `${start.toLocaleString()} → ${sameDay ? end.toLocaleTimeString() : end.toLocaleString()}`
}

/**
 * 剩余时长的人类可读形式，每一段都带单位。
 *
 * 别去切 .NET 那个 TimeSpan 字符串：它序列化成 `[d.]hh:mm:ss[.fffffff]`，天数后面那个点
 * 和小数秒前面那个点长得一模一样。曾经用 `split('.')[0]` 想砍掉小数秒，结果一超过一天就
 * 只剩下天数那个数字——3 天 5 小时显示成孤零零一个「~3 left」，没单位，也说不清是天是时。
 * 秒数是后端直接给的（etaSeconds），从它算起就没有这种歧义。
 *
 * 只显示两级：「3d 5h」比「3d 5h 20m 11s」好读，而剩三天的时候那 11 秒也没人在乎。
 */
export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (d > 0) return h > 0 ? `${d}d ${h}h` : `${d}d`
  if (h > 0) return m > 0 ? `${h}h ${m}m` : `${h}h`
  if (m > 0) return `${m}m ${s % 60}s`
  return `${s}s`
}

/** 字节数的人类可读形式。备份界面到处都要用（尺寸、速度），集中一处避免各写一份走样。 */
export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let v = n / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(1)} ${units[i]}`
}
