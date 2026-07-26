import { useState } from 'react'

type Freq = 'hourly' | 'daily' | 'weekly' | 'monthly'

const dowNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

function buildCron(freq: Freq, minute: number, hour: number, dow: number, dom: number): string {
  switch (freq) {
    case 'hourly':
      return `${minute} * * * *`
    case 'daily':
      return `${minute} ${hour} * * *`
    case 'weekly':
      return `${minute} ${hour} * * ${dow}`
    case 'monthly':
      return `${minute} ${hour} ${dom} * *`
  }
}

// 图形化 cron 编辑器（PRD 2.3）：常用频率可视化选择，高级用户可切换到手输。
export function CronEditor({ value, onChange }: { value: string; onChange: (cron: string) => void }) {
  const [advanced, setAdvanced] = useState(false)
  const [freq, setFreq] = useState<Freq>('daily')
  const [minute, setMinute] = useState(0)
  const [hour, setHour] = useState(2)
  const [dow, setDow] = useState(0)
  const [dom, setDom] = useState(1)

  const apply = (next: Partial<{ freq: Freq; minute: number; hour: number; dow: number; dom: number }>) => {
    const f = next.freq ?? freq
    const mi = next.minute ?? minute
    const h = next.hour ?? hour
    const w = next.dow ?? dow
    const d = next.dom ?? dom
    if (next.freq !== undefined) setFreq(next.freq)
    if (next.minute !== undefined) setMinute(next.minute)
    if (next.hour !== undefined) setHour(next.hour)
    if (next.dow !== undefined) setDow(next.dow)
    if (next.dom !== undefined) setDom(next.dom)
    onChange(buildCron(f, mi, h, w, d))
  }

  if (advanced) {
    return (
      <div className="row">
        <input
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="w-md mono"
          placeholder="min hour day-of-month month day-of-week"
        />
        <button type="button" onClick={() => setAdvanced(false)}>
          Simple
        </button>
      </div>
    )
  }

  return (
    <div className="row" style={{ flexWrap: 'wrap' }}>
      <select value={freq} onChange={(e) => apply({ freq: e.target.value as Freq })}>
        <option value="hourly">Hourly</option>
        <option value="daily">Daily</option>
        <option value="weekly">Weekly</option>
        <option value="monthly">Monthly</option>
      </select>

      {freq === 'weekly' && (
        <select value={dow} onChange={(e) => apply({ dow: Number(e.target.value) })}>
          {dowNames.map((n, i) => (
            <option key={n} value={i}>
              {n}
            </option>
          ))}
        </select>
      )}

      {freq === 'monthly' && (
        <label>
          Day{' '}
          <input
            type="number"
            min={1}
            max={31}
            value={dom}
            onChange={(e) => apply({ dom: Number(e.target.value) })}
            className="w-sm"
          />
        </label>
      )}

      {freq !== 'hourly' && (
        <label>
          at{' '}
          <input
            type="number"
            min={0}
            max={23}
            value={hour}
            onChange={(e) => apply({ hour: Number(e.target.value) })}
            className="w-sm"
          />
          h
        </label>
      )}

      <label>
        min{' '}
        <input
          type="number"
          min={0}
          max={59}
          value={minute}
          onChange={(e) => apply({ minute: Number(e.target.value) })}
          className="w-sm"
        />
      </label>

      <code>{value || buildCron(freq, minute, hour, dow, dom)}</code>

      <button type="button" onClick={() => setAdvanced(true)}>
        Advanced
      </button>
    </div>
  )
}
