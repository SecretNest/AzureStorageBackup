import { describe, expect, it } from 'vitest'
import { formatDuration } from './format'

describe('formatDuration', () => {
  it('每一段都带单位', () => {
    expect(formatDuration(45)).toBe('45s')
    expect(formatDuration(90)).toBe('1m 30s')
    expect(formatDuration(3600)).toBe('1h')
    expect(formatDuration(3900)).toBe('1h 5m')
  })

  it('超过一天说得清是天', () => {
    // 这正是那个 bug 的形状：.NET 把它序列化成 "3.05:20:00"，按 '.' 切第一段只剩 "3"，
    // 屏幕上就是没有单位的「~3 left」，看不出是 3 天还是 3 小时。
    expect(formatDuration(3 * 86400 + 5 * 3600 + 20 * 60)).toBe('3d 5h')
    expect(formatDuration(3 * 86400)).toBe('3d')
  })

  it('只显示两级——剩三天的时候没人在乎那几秒', () => {
    expect(formatDuration(3 * 86400 + 5 * 3600 + 20 * 60 + 11)).toBe('3d 5h')
    expect(formatDuration(5 * 3600 + 20 * 60 + 11)).toBe('5h 20m')
  })

  it('零与负数都落到 0s，不显示奇怪的东西', () => {
    expect(formatDuration(0)).toBe('0s')
    expect(formatDuration(-5)).toBe('0s')
  })
})
