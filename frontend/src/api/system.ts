import { api } from './client'

/** The clock cron expressions are read in, and whether anything will read them at all. */
export interface SchedulerInfo {
  /** The zone in effect: Scheduler__TimeZone, or UTC when it is unset or not recognised. */
  timeZone: string
  /** That zone's offset from UTC right now, in minutes (east positive). */
  utcOffsetMinutes: number
  /** What Scheduler__TimeZone is set to, null when unset. */
  configuredTimeZone: string | null
  /** False when configuredTimeZone was set but the server does not know it, so UTC is in effect by fallback. */
  recognised: boolean
  /** Scheduler__Enabled: with it off, schedules never fire (Run now still works). */
  enabled: boolean
}

export const systemApi = {
  paths: () => api.get<Record<string, string>>('/system/paths'),
  version: () => api.get<{ version: string }>('/system/version'),
  scheduler: () => api.get<SchedulerInfo>('/system/scheduler'),
}
