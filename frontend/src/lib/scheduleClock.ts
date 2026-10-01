import type { SchedulerInfo } from '../api/system'

export interface ScheduleClockLine {
  text: string
  /** Amber: something the operator set is not doing what they meant. */
  warning: boolean
}

/** "UTC+08:00", "UTC-03:30"; the sign is always written so that an offset of zero reads as one too. */
export function formatUtcOffset(minutes: number): string {
  const sign = minutes < 0 ? '-' : '+'
  const abs = Math.abs(minutes)
  const hh = String(Math.floor(abs / 60)).padStart(2, '0')
  const mm = String(abs % 60).padStart(2, '0')
  return `UTC${sign}${hh}:${mm}`
}

/**
 * What the Schedules page says about the clock its cron hours are read in.
 *
 * A cron hour is meaningless until the reader knows the zone, and the server is the only party that
 * knows it: Scheduler__TimeZone, UTC when unset. The same UTC also results, silently, from a value the
 * server does not recognise — a typo there fires every schedule at the wrong hour, every night, with
 * no other sign — so that case is called out as a warning rather than reported as plain UTC. The
 * second line, when present, says the scheduler itself is switched off: a schedule on such a server
 * never fires however right its hour is, and the page is where someone wondering why would look.
 */
export function scheduleClockLines(info: SchedulerInfo): ScheduleClockLine[] {
  const lines: ScheduleClockLine[] = []
  if (!info.recognised) {
    lines.push({
      text: `Times are in UTC: the server's Scheduler__TimeZone is "${info.configuredTimeZone}", which it does not recognise.`,
      warning: true,
    })
  } else if (info.configuredTimeZone === null) {
    lines.push({
      text: 'Times are in UTC. Set Scheduler__TimeZone on the server to use another zone.',
      warning: false,
    })
  } else if (info.timeZone === 'UTC') {
    lines.push({ text: 'Times are in UTC.', warning: false })
  } else {
    lines.push({
      text: `Times are in ${info.timeZone} (${formatUtcOffset(info.utcOffsetMinutes)}), the server's Scheduler__TimeZone.`,
      warning: false,
    })
  }
  if (!info.enabled) {
    lines.push({
      text: 'The scheduler is switched off on this server (Scheduler__Enabled=false): schedules do not fire by themselves. Run now still works.',
      warning: true,
    })
  }
  return lines
}
