import { describe, expect, it } from 'vitest'
import type { SchedulerInfo } from '../api/system'
import { formatUtcOffset, scheduleClockLines } from './scheduleClock'

const base: SchedulerInfo = {
  timeZone: 'UTC',
  utcOffsetMinutes: 0,
  configuredTimeZone: null,
  recognised: true,
  enabled: true,
}

describe('formatUtcOffset', () => {
  it('writes the sign, hours and minutes, zero included', () => {
    expect(formatUtcOffset(480)).toBe('UTC+08:00')
    expect(formatUtcOffset(-210)).toBe('UTC-03:30')
    expect(formatUtcOffset(0)).toBe('UTC+00:00')
  })
})

describe('scheduleClockLines', () => {
  it('reads UTC by default and says how to change it', () => {
    expect(scheduleClockLines(base)).toEqual([
      { text: 'Times are in UTC. Set Scheduler__TimeZone on the server to use another zone.', warning: false },
    ])
  })

  it('names the configured zone with its current offset', () => {
    const lines = scheduleClockLines({
      ...base,
      timeZone: 'Asia/Shanghai',
      utcOffsetMinutes: 480,
      configuredTimeZone: 'Asia/Shanghai',
    })
    expect(lines).toEqual([
      { text: "Times are in Asia/Shanghai (UTC+08:00), the server's Scheduler__TimeZone.", warning: false },
    ])
  })

  it('says plain UTC when UTC was set on purpose', () => {
    expect(scheduleClockLines({ ...base, configuredTimeZone: 'UTC' })).toEqual([
      { text: 'Times are in UTC.', warning: false },
    ])
  })

  it('warns when the configured zone was not recognised instead of passing it off as UTC', () => {
    const [line] = scheduleClockLines({ ...base, configuredTimeZone: 'Asia/Shangai', recognised: false })
    expect(line.warning).toBe(true)
    expect(line.text).toContain('"Asia/Shangai"')
    expect(line.text).toContain('does not recognise')
  })

  it('adds a warning line when the scheduler is switched off', () => {
    const lines = scheduleClockLines({ ...base, enabled: false })
    expect(lines).toHaveLength(2)
    expect(lines[1].warning).toBe(true)
    expect(lines[1].text).toContain('Scheduler__Enabled=false')
    expect(lines[1].text).toContain('Run now still works')
  })
})
