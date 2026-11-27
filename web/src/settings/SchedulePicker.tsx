import { Field, Input, Radio, RadioGroup, Select, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { locale, t } from '../i18n'

export type Unit = 'h' | 'd' | 'w'
export type Schedule = { kind: 'every' | 'cron'; count: number; unit: Unit; weekday: number; cron: string }

const WEEKDAYS = () => [t('Sonntag'), t('Montag'), t('Dienstag'), t('Mittwoch'), t('Donnerstag'), t('Freitag'), t('Samstag')]

/**
 * Zeitplan der Sicherung (Kennung backupcron): „alle N Stunden, Tage oder Wochen“ oder eine eigene Cron-Regel.
 * Darunter stehen immer die nächsten drei Läufe - so sieht man sofort, ob die Regel tut, was man meint.
 */
export function SchedulePicker({ preview, value, at, onAt, onChange }: { preview: string; value: Schedule; at: string; onAt: (at: string) => void; onChange: (s: Schedule) => void }) {
  const [v, setV] = useState(value)
  const [cron, setCron] = useState(value.cron)
  const [next, setNext] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setV(value); setCron(value.cron) }, [JSON.stringify(value)]) // eslint-disable-line react-hooks/exhaustive-deps
  // Vorschau: bei jeder Änderung (Regel mit kurzer Pause beim Tippen)
  useEffect(() => {
    const h = setTimeout(() => {
      api.post<{ next: string[] }>(preview, { scheduleKind: v.kind, at, everyCount: v.count, everyUnit: v.unit, weekday: v.weekday, cron })
        .then(x => { setNext(x.next); setError(null) }).catch(e => { setNext(null); setError(e instanceof Error ? e.message : String(e)) })
    }, 300)
    return () => clearTimeout(h)
  }, [v.kind, v.count, v.unit, v.weekday, cron, at, preview])
  const set = (patch: Partial<Schedule>) => { const n = { ...v, ...patch }; setV(n); onChange(n) }
  const fmt = (x: string) => new Date(x).toLocaleString(locale, { weekday: 'short', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <RadioGroup layout="horizontal" value={v.kind} onChange={(_, d) => set({ kind: d.value as Schedule['kind'] })}>
        <Radio value="every" label={t('Fester Abstand')} />
        <Radio value="cron" label={t('Eigene Regel (Cron)')} />
      </RadioGroup>
      {v.kind === 'every' ? (
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <Field label={t('Alle')}><Input type="number" min={1} max={999} value={String(v.count)} style={{ width: 80 }}
            onChange={(_, d) => setV({ ...v, count: Math.max(1, Math.min(999, Math.round(Number(d.value)) || 1)) })} onBlur={() => onChange(v)} /></Field>
          <Field label={t('Einheit')}>
            <Select value={v.unit} onChange={(_, d) => set({ unit: d.value as Unit })}>
              <option value="h">{t('Stunden')}</option><option value="d">{t('Tage')}</option><option value="w">{t('Wochen')}</option>
            </Select>
          </Field>
          {v.unit === 'w' && <Field label={t('Wochentag')}>
            <Select value={String(v.weekday)} onChange={(_, d) => set({ weekday: Number(d.value) })}>
              {WEEKDAYS().map((n, i) => <option key={i} value={i}>{n}</option>)}
            </Select>
          </Field>}
          <Field label={v.unit === 'h' ? t('ab Uhrzeit') : t('Uhrzeit')}><Input type="time" value={at} onChange={(_, d) => onAt(d.value)} style={{ width: 120 }} /></Field>
        </div>
      ) : (
        <Field label={t('Regel')} hint={t('Minute Stunde Tag Monat Wochentag, etwa „0 3 * * MON#1“ für jeden ersten Montag im Monat um 3 Uhr oder „30 2 * * 1-5“ für montags bis freitags um 2:30 Uhr.')}>
          <Input value={cron} onChange={(_, d) => setCron(d.value)} onBlur={() => { if (!error) onChange({ ...v, cron }) }} style={{ maxWidth: 260, fontFamily: 'monospace' }} />
        </Field>
      )}
      {error ? <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>
        : next?.length === 0 ? <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Bitte die Regel prüfen: Sie ergibt keinen nächsten Lauf.')}</Text>
        : next && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Nächste Läufe: {list}', { list: next.map(fmt).join(' · ') })}</Text>}
    </div>
  )
}
